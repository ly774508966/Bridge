﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Mono.Cecil;
using System.Text.RegularExpressions;
using ICSharpCode.NRefactory.CSharp;
using System.Linq;
using Newtonsoft.Json;
using Ext.Net.Utilities;
using ICSharpCode.NRefactory.TypeSystem;
using System.Globalization;
using ICSharpCode.NRefactory.TypeSystem.Implementation;
using ICSharpCode.NRefactory.Semantics;

namespace Bridge.NET
{
    public partial class Emitter : Visitor
    {
        public Emitter(IDictionary<string, TypeDefinition> typeDefinitions, List<TypeInfo> types, Validator validator)
        {
            this.TypeDefinitions = typeDefinitions;
            this.Types = types;
            this.Types.Sort(this.CompareTypeInfos);

            this.Output = new StringBuilder();
            this.Level = 0;
            this.IsNewLine = true;
            this.EnableSemicolon = true;
            this.Validator = validator;
        }
        
        protected virtual void EnsureComma()
        {
            if (this.Comma)
            {
                this.WriteComma(true);
                this.Comma = false;
            }
        }

        protected virtual HashSet<string> CreateNamespaces()
        {
            var result = new HashSet<string>();

            foreach (string typeName in this.TypeDefinitions.Keys)
            {
                int index = typeName.LastIndexOf('.');

                if (index >= 0)
                {
                    this.RegisterNamespace(typeName.Substring(0, index), result);
                }
            }

            return result;
        }

        protected virtual void RegisterNamespace(string ns, ICollection<string> repository)
        {
            if (String.IsNullOrEmpty(ns) || repository.Contains(ns))
            {
                return;
            }

            string[] parts = ns.Split('.');
            StringBuilder builder = new StringBuilder();

            foreach (string part in parts)
            {
                if (builder.Length > 0)
                {
                    builder.Append('.');
                }

                builder.Append(part);
                string item = builder.ToString();

                if (!repository.Contains(item))
                {
                    repository.Add(item);
                }
            }
        }        

        protected virtual int CompareTypeInfos(TypeInfo x, TypeInfo y)
        {
            if (x == y)
            {
                return 0;
            }

            if (x.FullName == Emitter.ROOT)
            {
                return -1;
            }

            if (y.FullName == Emitter.ROOT)
            {
                return 1;
            }

            var xTypeDefinition = this.TypeDefinitions[Helpers.GetTypeMapKey(x)];
            var yTypeDefinition = this.TypeDefinitions[Helpers.GetTypeMapKey(y)];

            if (Helpers.IsSubclassOf(xTypeDefinition, yTypeDefinition))
            {
                return 1;
            }

            if (Helpers.IsSubclassOf(yTypeDefinition, xTypeDefinition))
            {
                return -1;
            }

            if (xTypeDefinition.IsInterface && Helpers.IsImplementationOf(yTypeDefinition, xTypeDefinition))
            {
                return -1;
            }

            if (yTypeDefinition.IsInterface && Helpers.IsImplementationOf(xTypeDefinition, yTypeDefinition))
            {
                return 1;
            }

            return Comparer.Default.Compare(x.FullName, y.FullName);
        }

        public virtual void Emit()
        {
            this.Writers = new Stack<Tuple<string, StringBuilder, bool>>();
            Bridge.NET.MemberResolver.InitResolver(this.SourceFiles, this.AssemblyReferences);
            
            foreach (var type in this.Types)
            {
                this.TypeInfo = type;

                this.EmitClassHeader();
                this.EmitStaticBlock();
                this.EmitInstantiableBlock();
                this.EmitClassEnd();
                this.Comma = false;
            }            
        }

        protected virtual void EmitClassEnd()
        {
            this.WriteNewLine();
            this.EndBlock();
            this.WriteCloseParentheses();
            this.WriteSemiColon();
            this.WriteNewLine();
            this.WriteNewLine();
        }
        
        protected virtual void EmitClassHeader()
        {
            TypeDefinition baseType = this.GetBaseTypeDefinition();           
            
            this.Write(Emitter.ROOT + ".Class.extend");
            this.WriteOpenParentheses();
            this.Write("'" + this.ShortenTypeName(this.TypeInfo.FullName), "', ");
            this.BeginBlock();

            string extend = this.GetTypeHierarchy();

            if (extend.IsNotEmpty() && !this.TypeInfo.IsEnum) 
            { 
                this.Write("$extend");
                this.WriteColon();
                this.Write(extend);
                this.Comma = true;
            }
        }

        protected virtual void EmitStaticBlock()
        {
            if (this.TypeInfo.HasStatic)
            {
                this.EnsureComma();
                this.Write("$statics");
                this.WriteColon();
                this.BeginBlock();

                this.EmitCtorForStaticClass();
                this.EmitMethods(this.TypeInfo.StaticMethods, this.TypeInfo.StaticProperties);

                this.WriteNewLine();
                this.EndBlock();
                this.Comma = true;
            }            
        }

        protected virtual void EmitInstantiableBlock()
        {
            if (this.TypeInfo.HasInstantiable)
            {
                this.EnsureComma();
                this.EmitCtorForInstantiableClass();
                this.EmitMethods(this.TypeInfo.InstanceMethods, this.TypeInfo.InstanceProperties);
            }
            else
            {
                this.Comma = false;
            }
        }

        protected virtual void EmitMethods(Dictionary<string, MethodDeclaration> methods, Dictionary<string, PropertyDeclaration> properties)
        {
            var names = new List<string>(properties.Keys);
            names.Sort();

            foreach (var name in names)
            {
                this.VisitPropertyDeclaration(properties[name]);
            }

            names = new List<string>(methods.Keys);
            names.Sort();

            foreach (var name in names)
            {
                this.VisitMethodDeclaration(methods[name]);
            }
        }

        protected virtual void EmitCtorForInstantiableClass()
        {
            var ctor = this.TypeInfo.Ctor ?? new ConstructorDeclaration
            {
                Modifiers = Modifiers.Public,
                Body = new BlockStatement()
            };

            var baseType = this.GetBaseTypeDefinition();
            this.ResetLocals();
            this.AddLocals(ctor.Parameters);

            this.Write("$init");
            this.WriteColon();
            this.WriteFunction();

            this.EmitMethodParameters(ctor.Parameters, ctor);
            
            this.WriteSpace();
            this.BeginBlock();

            var requireNewLine = false;
            var changeCase = this.ChangeCase;

            if (this.TypeInfo.InstanceFields.Count > 0)
            {
                this.EmitInstanceFields(changeCase);
                requireNewLine = true;
            }            

            if (baseType != null && !this.Validator.IsIgnoreType(baseType))
            {
                if (requireNewLine)
                {
                    this.WriteNewLine();
                }
                this.EmitBaseConstructor(ctor);
                requireNewLine = true;
            }

            if (this.TypeInfo.Events.Count > 0)
            {
                if (requireNewLine)
                {
                    this.WriteNewLine();
                }
                this.EmitEvents(this.TypeInfo.Events);
                requireNewLine = true;
            }

            var script = this.GetScript(ctor);

            if (script == null)
            {
                if (ctor.Body.HasChildren)
                {
                    if (requireNewLine)
                    {
                        this.WriteNewLine();
                    }

                    ctor.Body.AcceptChildren(this);
                }
            }
            else
            {
                if (requireNewLine)
                {
                    this.WriteNewLine();
                }

                foreach (var line in script)
                {
                    this.Write(line);
                    this.WriteNewLine();
                }
            }

            this.EndBlock();
            this.Comma = true;
        }

        protected virtual void EmitEvents(IEnumerable<EventDeclaration> events)
        {
            foreach (var evt in events)
            {
                string name = this.GetEntityName(evt);

                this.Write("this.", name, " = new Bridge.Event()");                
                this.WriteSemiColon();
                this.WriteNewLine();
            }
        }

        protected virtual void EmitBaseConstructor(ConstructorDeclaration ctor)
        {
            var initializer = ctor.Initializer ?? new ConstructorInitializer()
            {
                ConstructorInitializerType = ConstructorInitializerType.Base
            };

            if (initializer.ConstructorInitializerType == ConstructorInitializerType.This)
            {
                throw CreateException(ctor, "Multiple constructors are not supported");
            }

            this.Write("this.base");
            this.WriteOpenParentheses();

            foreach (var p in initializer.Arguments)
            {
                this.WriteComma();
                p.AcceptVisitor(this);
            }

            this.WriteCloseParentheses();
            this.WriteSemiColon();
            this.WriteNewLine();
        }

        protected virtual void EmitInstanceFields(bool changeCase)
        {
            var names = new List<string>(this.TypeInfo.InstanceFields.Keys);
            names.Sort();

            foreach (var name in names)
            {
                string fieldName = name;

                if (this.TypeInfo.FieldsDeclarations.ContainsKey(name))
                {
                    fieldName = this.GetEntityName(this.TypeInfo.FieldsDeclarations[name]);
                }
                else
                {
                    fieldName = changeCase ? Ext.Net.Utilities.StringUtils.ToLowerCamelCase(name) : name;
                }

                this.Write("this.", fieldName, " = ");
                this.TypeInfo.InstanceFields[name].AcceptVisitor(this);
                this.WriteSemiColon();
                this.WriteNewLine();
            }
        }

        protected virtual void EmitCtorForStaticClass()
        {
            if (this.TypeInfo.StaticCtor != null || this.TypeInfo.StaticFields.Count > 0 || this.TypeInfo.Consts.Count > 0)
            {
                var sortedNames = this.TypeInfo.StaticFields.Count > 0 ? new List<string>(this.TypeInfo.StaticFields.Keys) : new List<string>();
                if (this.TypeInfo.Consts.Count > 0)
                {
                    sortedNames.AddRange(this.TypeInfo.Consts.Keys);
                }
                sortedNames.Sort();

                this.Write("$init");
                this.WriteColon();
                this.WriteFunction();
                this.WriteOpenCloseParentheses(true);

                this.BeginBlock();

                var changeCase = this.ChangeCase;

                for (var i = 0; i < sortedNames.Count; i++)
                {
                    var fieldName = sortedNames[i];
                    var isField = this.TypeInfo.StaticFields.ContainsKey(fieldName);
                    string name = null;

                    if (this.TypeInfo.FieldsDeclarations.ContainsKey(fieldName))
                    {
                        name = this.GetEntityName(this.TypeInfo.FieldsDeclarations[fieldName], !isField);
                    }
                    else
                    {
                        name = (changeCase && isField) ? Ext.Net.Utilities.StringUtils.ToLowerCamelCase(fieldName) : fieldName;
                        if (Emitter.IsReservedStaticName(name))
                        {
                            name = "$" + name;
                        }
                    }

                    this.Write("this.", name, " = ");

                    if (isField)
                    {
                        this.TypeInfo.StaticFields[fieldName].AcceptVisitor(this);
                    }
                    else
                    {
                        this.TypeInfo.Consts[fieldName].AcceptVisitor(this);
                    }
                    
                    this.WriteSemiColon();
                    this.WriteNewLine();
                }

                if (this.TypeInfo.StaticCtor != null)
                {
                    var ctor = this.TypeInfo.StaticCtor;

                    if (ctor.Body.HasChildren)
                    {
                        ctor.Body.AcceptChildren(this);
                    }
                }

                this.EndBlock();
                this.Comma = true;
            }            
        }

        private static bool IsReservedStaticName(string name)
        {
            return Emitter.reservedStaticNames.Any(n => String.Equals(name, n, StringComparison.InvariantCultureIgnoreCase));
        }

        protected virtual string GetTypeHierarchy()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[");

            var list = new List<string>();
            var baseType = this.GetBaseTypeDefinition();

            if (baseType != null)
            {
                list.Add(Helpers.GetScriptFullName(baseType));
            }

            foreach (Mono.Cecil.TypeReference i in this.GetTypeDefinition().Interfaces)
            {
                if (i.FullName == "System.Collections.IEnumerable")
                {
                    continue;
                }

                if (i.FullName == "System.Collections.IEnumerator")
                {
                    continue;
                }

                list.Add(Helpers.GetScriptFullName(i));
            }

            if (list.Count == 1 && baseType.FullName == "System.Object")
            {
                return "";
            }

            bool needComma = false;

            foreach (var item in list)
            {
                if (needComma)
                {
                    sb.Append(",");
                }

                needComma = true;
                sb.Append(this.ShortenTypeName(item));
            }

            sb.Append("]");

            return sb.ToString();
        }

        protected virtual void EmitLambda(IEnumerable<ParameterDeclaration> parameters, AstNode body, AstNode context)
        {
            this.PushLocals();
            this.AddLocals(parameters);

            bool block = body is BlockStatement;


            /// TODO: Is this next line required?
            this.Write("");

            var savedPos = this.Output.Length;
            var savedThisCount = this.ThisRefCounter;

            this.WriteFunction();
            this.EmitMethodParameters(parameters, context);
            this.WriteSpace();

            if (!block)
            {
                this.WriteOpenBrace();
                this.WriteSpace();
            }

            if (body.Parent is LambdaExpression && !block)
            {
                this.WriteReturn(true);
            }

            body.AcceptVisitor(this);

            if (!block)
            {
                this.WriteSpace();
                this.WriteCloseBrace();
            }

            if (this.ThisRefCounter > savedThisCount)
            {
                this.Output.Insert(savedPos, Emitter.ROOT + "." + Emitter.BIND + "(this, ");
                this.WriteCloseParentheses();
            }

            this.PopLocals();
        }

        protected virtual void EmitBlockOrIndentedLine(AstNode node)
        {
            bool block = node is BlockStatement;

            if (!block)
            {
                this.WriteNewLine();
                this.Indent();
            }
            else
            {
                this.WriteSpace();
            }

            node.AcceptVisitor(this);

            if (!block)
            {
                this.Outdent();
            }
        }

        protected virtual void EmitMethodParameters(IEnumerable<ParameterDeclaration> declarations, AstNode context)
        {
            this.WriteOpenParentheses();
            bool needComma = false;

            foreach (var p in declarations)
            {
                this.CheckIdentifier(p.Name, context);

                if (needComma)
                {
                    this.WriteComma();
                }

                needComma = true;
                this.Write(p.Name.Replace(Emitter.FIX_ARGUMENT_NAME, ""));
            }

            this.WriteCloseParentheses();
        }

        protected virtual void EmitExpressionList(IEnumerable<Expression> expressions)
        {
            bool needComma = false;
            int count = this.Writers.Count;

            foreach (var expr in expressions)
            {
                if (needComma)
                {
                    this.WriteComma();
                }

                needComma = true;
                expr.AcceptVisitor(this);

                if (this.Writers.Count != count)
                {
                    this.PopWriter();
                }
            }
        }

        protected virtual void Indent()
        {
            ++this.Level;
        }

        protected virtual void Outdent()
        {
            if (this.Level > 0)
            {
                this.Level--;
            }
        }

        protected virtual void WriteIndent()
        {
            if (!this.IsNewLine)
            {
                return;
            }

            for (var i = 0; i < this.Level; i++)
            {
                this.Output.Append("    ");
            }

            this.IsNewLine = false;
        }

        protected virtual void WriteNewLine()
        {
            this.Output.Append('\n');
            this.IsNewLine = true;
        }

        protected virtual void BeginBlock()
        {
            this.WriteOpenBrace();
            this.WriteNewLine();
            this.Indent();
        }

        protected virtual void EndBlock()
        {
            this.Outdent();
            this.WriteCloseBrace();
        }

        protected virtual void Write(object value)
        {
            this.WriteIndent();
            this.Output.Append(value);
        }

        protected virtual void Write(params object[] values)
        {
            foreach (var item in values)
            {
                this.Write(item);
            }
        }

        protected virtual void WriteScript(object value)
        {
            this.WriteIndent();
            this.Output.Append(this.ToJavaScript(value));
        }

        protected virtual void WriteComment(string text)
        {
            this.Write("/* " + text + " */");
            this.WriteNewLine();
        }

        protected virtual void WriteComma()
        {
            this.WriteComma(false);
        }

        protected virtual void WriteComma(bool newLine)
        {
            this.Write(",");

            if (newLine)
            {
                this.WriteNewLine();
            }
            else
            {
                this.WriteSpace();
            }
        }

        protected virtual void WriteThis()
        {
            this.Write("this");
            this.ThisRefCounter++;
        }

        protected virtual void WriteSpace()
        {
            this.WriteSpace(true);
        }

        protected virtual void WriteSpace(bool addSpace)
        {
            if (addSpace)
            {
                this.Write(" ");
            }
        }

        protected virtual void WriteDot()
        {
            this.Write(".");
        }

        protected virtual void WriteColon()
        {
            this.Write(": ");
        }

        protected virtual void WriteSemiColon()
        {
            this.WriteSemiColon(false);
        }

        protected virtual void WriteSemiColon(bool newLine)
        {
            this.Write(";");

            if (newLine)
            {
                this.WriteNewLine();
            }
        }

        protected virtual void WriteNew()
        {
            this.Write("new ");
        }

        protected virtual void WriteVar()
        {
            this.Write("var ");
        }

        protected virtual void WriteIf()
        {
            this.Write("if ");
        }

        protected virtual void WriteElse()
        {
            this.Write("else ");
        }

        protected virtual void WriteWhile()
        {
            this.Write("while ");
        }

        protected virtual void WriteFor()
        {
            this.Write("for ");
        }

        protected virtual void WriteThrow()
        {
            this.Write("throw ");
        }

        protected virtual void WriteTry()
        {
            this.Write("try ");
        }

        protected virtual void WriteCatch()
        {
            this.Write("catch ");
        }

        protected virtual void WriteFinally()
        {
            this.Write("finally ");
        }

        protected virtual void WriteDo()
        {
            this.Write("do ");
        }

        protected virtual void WriteSwitch()
        {
            this.Write("switch ");
        }

        protected virtual void WriteReturn(bool addSpace)
        {
            this.Write("return");
            this.WriteSpace(addSpace);
        }

        protected virtual void WriteOpenBracket()
        {
            this.WriteOpenBracket(false);
        }

        protected virtual void WriteOpenBracket(bool addSpace)
        {
            this.Write("[");
            this.WriteSpace(addSpace);
        }

        protected virtual void WriteCloseBracket()
        {
            this.WriteCloseBracket(false);
        }

        protected virtual void WriteCloseBracket(bool addSpace)
        {
            this.WriteSpace(addSpace);
            this.Write("]");
        }

        protected virtual void WriteOpenParentheses()
        {
            this.WriteOpenParentheses(false);
        }

        protected virtual void WriteOpenParentheses(bool addSpace)
        {
            this.Write("(");
            this.WriteSpace(addSpace);
        }

        protected virtual void WriteCloseParentheses()
        {
            this.WriteCloseParentheses(false);
        }

        protected virtual void WriteCloseParentheses(bool addSpace)
        {
            this.WriteSpace(addSpace);
            this.Write(")");
        }

        protected virtual void WriteOpenCloseParentheses()
        {
            this.WriteOpenCloseParentheses(false);
        }

        protected virtual void WriteOpenCloseParentheses(bool addSpace)
        {
            this.Write("()");
            this.WriteSpace(addSpace);
        }

        protected virtual void WriteOpenBrace()
        {
            this.WriteOpenBrace(false);
        }

        protected virtual void WriteOpenBrace(bool addSpace)
        {
            this.Write("{");
            this.WriteSpace(addSpace);
        }

        protected virtual void WriteCloseBrace()
        {
            this.WriteCloseBrace(false);
        }

        protected virtual void WriteCloseBrace(bool addSpace)
        {
            this.WriteSpace(addSpace);
            this.Write("}");
        }

        protected virtual void WriteOpenCloseBrace()
        {
            this.Write("{ }");
        }

        protected virtual void WriteFunction()
        {
            this.Write("function ");
        }

        protected virtual void WriteObjectInitializer(IEnumerable<Expression> expressions)
        {
            bool needComma = false;

            foreach (Expression item in expressions)
            {
                NamedExpression namedExression = item as NamedExpression;
                NamedArgumentExpression namedArgumentExpression = item as NamedArgumentExpression;
                
                if (needComma)
                {
                    this.WriteComma();
                }

                needComma = true;
                string name = namedExression != null ? namedExression.Name : namedArgumentExpression.Name;
                Expression expression = namedExression != null ? namedExression.Expression : namedArgumentExpression.Expression;

                this.Write(name, ": ");
                expression.AcceptVisitor(this);
            }
        }

        protected virtual string ToJavaScript(object value)
        {
            return JsonConvert.SerializeObject(value);
        }

        protected virtual bool KeepLineAfterBlock(BlockStatement block)
        {
            var parent = block.Parent;

            if (parent is AnonymousMethodExpression)
            {
                return true;
            }

            if (parent is LambdaExpression)
            {
                return true;
            }

            if (parent is MethodDeclaration)
            {
                return true;
            }

            var loop = parent as DoWhileStatement;

            if (loop != null)
            {
                return true;
            }

            return false;
        }

        private static HashSet<string> InvalidIdentifiers = new HashSet<string>(new[] 
        { 
            "_", 
            "arguments",
            "boolean", 
            "debugger", 
            "delete", 
            "export", 
            "extends", 
            "final", 
            "function",
            "implements", 
            "import", 
            "instanceof", 
            "native", 
            "package", 
            "super",
            "synchronized", 
            "throws", 
            "transient", 
            "var", 
            "with"                
        });

        protected virtual void CheckIdentifier(string name, AstNode context)
        {
            if (Emitter.InvalidIdentifiers.Contains(name))
            {
                throw this.CreateException(context, "Cannot use '" + name + "' as identifier");
            }
        }

        protected virtual string GetNextIteratorName()
        {
            var index = this.IteratorCount++;
            var result = "$i";

            if (index > 0)
            {
                result += index;
            }

            return result;
        }

        protected virtual IMemberDefinition ResolveFieldOrMethod(string name, int genericCount)
        {
            bool allowPrivate = true;
            TypeDefinition type = this.GetTypeDefinition();
            TypeDefinition thisType = type;

            while (true)
            {
                foreach (MethodDefinition method in type.Methods)
                {
                    if (method.Name != name || method.GenericParameters.Count != genericCount)
                    {
                        continue;
                    }

                    if (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly)
                    {
                        return method;
                    }

                    if (method.IsPrivate && allowPrivate)
                    {
                        return method;
                    }

                    if (method.IsAssembly && type.Module.Mvid == thisType.Module.Mvid)
                    {
                        return method;
                    }
                }

                foreach (FieldDefinition field in type.Fields)
                {
                    if (field.Name != name)
                    {
                        continue;
                    }

                    if (field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly)
                    {
                        return field;
                    }

                    if (field.IsPrivate && allowPrivate)
                    {
                        return field;
                    }

                    if (field.IsAssembly && type.Module.Mvid == thisType.Module.Mvid)
                    {
                        return field;
                    }
                }

                type = this.GetBaseTypeDefinition(type);

                if (type == null)
                {
                    break;
                }

                allowPrivate = false;
            }

            return null;
        }

        protected virtual string ResolveNamespaceOrType(string id, bool allowNamespaces)
        {
            if (allowNamespaces && this.Namespaces.Contains(id))
            {
                return id;
            }

            if (this.TypeDefinitions.ContainsKey(id))
            {
                return id;
            }

            string guess;
            string namespacePrefix = this.TypeInfo.Namespace;

            if (!String.IsNullOrEmpty(namespacePrefix))
            {
                while (true)
                {
                    guess = namespacePrefix + "." + id;

                    if (allowNamespaces && this.Namespaces.Contains(guess))
                    {
                        return guess;
                    }

                    if (this.TypeDefinitions.ContainsKey(guess))
                    {
                        return guess;
                    }

                    int index = namespacePrefix.LastIndexOf(".");

                    if (index < 0)
                    {
                        break;
                    }

                    namespacePrefix = namespacePrefix.Substring(0, index);
                }
            }

            foreach (string usingPrefix in this.TypeInfo.Usings)
            {
                guess = usingPrefix + "." + id;

                if (this.TypeDefinitions.ContainsKey(guess))
                {
                    return guess;
                }
            }

            return null;
        }

        protected virtual string ResolveType(string id)
        {
            return this.ResolveNamespaceOrType(id, false);
        }

        protected virtual TypeDefinition GetTypeDefinition()
        {
            return this.TypeDefinitions[Helpers.GetTypeMapKey(this.TypeInfo)];
        }

        protected virtual TypeDefinition GetTypeDefinition(AstType reference)
        {
            string name = Helpers.GetScriptName(reference, true);
            name = this.ResolveType(name);

            return this.TypeDefinitions[name];
        }

        protected virtual TypeDefinition GetBaseTypeDefinition()
        {
            return this.GetBaseTypeDefinition(this.GetTypeDefinition());
        }

        protected virtual TypeDefinition GetBaseTypeDefinition(TypeDefinition type)
        {
            var reference = this.TypeDefinitions[Helpers.GetTypeMapKey(type)].BaseType;

            if (reference == null)
            {
                return null;
            }

            return this.TypeDefinitions[Helpers.GetTypeMapKey(reference)];
        }

        protected virtual TypeDefinition GetBaseMethodOwnerTypeDefinition(string methodName, int genericParamCount)
        {
            TypeDefinition type = this.GetBaseTypeDefinition();

            while (true)
            {
                var methods = type.Methods.Where(m => m.Name == methodName);

                foreach (var method in methods)
                {
                    if (genericParamCount < 1 || method.GenericParameters.Count == genericParamCount)
                    {
                        return type;
                    }
                }

                type = this.GetBaseTypeDefinition(type);
            }
        }

        protected virtual string ShortenTypeName(string name)
        {
            var type = this.TypeDefinitions[name];
            var customName = this.Validator.GetCustomTypeName(type);

            return !String.IsNullOrEmpty(customName) ? customName : name;
        }

        protected virtual ICSharpCode.NRefactory.CSharp.Attribute GetAttribute(AstNodeCollection<AttributeSection> attributes, string name)
        {
            string fullName = name + "Attribute";
            foreach (var i in attributes)
            {
                foreach (var j in i.Attributes)
                {
                    if (j.Type.ToString() == name)
                    {
                        return j;
                    }

                    var resolveResult = MemberResolver.ResolveNode(j);
                    if (resolveResult != null && !resolveResult.IsError && resolveResult.Type.FullName == fullName)
                    {
                        return j;
                    }
                }
            }

            return null;
        }

        protected virtual CustomAttribute GetAttribute(IEnumerable<CustomAttribute> attributes, string name)
        {
            foreach (var attr in attributes)
            {
                if (attr.AttributeType.FullName == name)
                {
                    return attr;
                }
            }

            return null;
        }

        protected virtual IAttribute GetAttribute(IEnumerable<IAttribute> attributes, string name)
        {
            foreach (var attr in attributes)
            {
                if (attr.AttributeType.FullName == name)
                {
                    return attr;
                }
            }

            return null;
        }

        protected virtual bool HasDelegateAttribute(MethodDeclaration method)
        {
            return this.GetAttribute(method.Attributes, "Delegate") != null;
        }

        protected virtual Tuple<bool, string> GetInlineCode(InvocationExpression node)
        {
            var parts = new List<string>();
            Expression current = node.Target;
            var genericCount = -1;

            while (true)
            {
                MemberReferenceExpression member = current as MemberReferenceExpression;

                if (member != null)
                {
                    parts.Insert(0, member.MemberName);
                    current = member.Target;

                    if (genericCount < 0)
                    {
                        genericCount = member.TypeArguments.Count;
                    }

                    continue;
                }

                IdentifierExpression id = current as IdentifierExpression;

                if (id != null)
                {
                    parts.Insert(0, id.Identifier);

                    if (genericCount < 0)
                    {
                        genericCount = id.TypeArguments.Count;
                    }

                    break;
                }

                TypeReferenceExpression typeRef = current as TypeReferenceExpression;

                if (typeRef != null)
                {
                    parts.Insert(0, Helpers.GetScriptName(typeRef.Type, false));
                    break;
                }

                break;
            }

            if (parts.Count < 1)
            {
                return null;
            }

            if (genericCount < 0)
            {
                genericCount = 0;
            }

            string typeName = parts.Count < 2
                ? this.TypeInfo.FullName
                : this.ResolveType(String.Join(".", parts.ToArray(), 0, parts.Count - 1));

            if (String.IsNullOrEmpty(typeName))
            {
                return null;
            }

            string methodName = parts[parts.Count - 1];

            TypeDefinition type = this.TypeDefinitions[typeName];
            var methods = type.Methods.Where(m => m.Name == methodName);

            foreach (var method in methods)
            {
                if (method.Parameters.Count == node.Arguments.Count
                    && method.GenericParameters.Count == genericCount)
                {
                    return new Tuple<bool,string>(method.IsStatic, this.Validator.GetInlineCode(method));

                }
            }

            return null;
        }

        protected virtual IEnumerable<string> GetScript(EntityDeclaration method)
        {
            var attr = this.GetAttribute(method.Attributes, Translator.CLR_ASSEMBLY + ".Script");

            return this.GetScriptArguments(attr);
        }

        protected virtual string GetMethodName(MethodDefinition method)
        {
            bool changeCase = !this.IsNativeMember(method.FullName) ? this.ChangeCase : true;
            var attr = method.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == Translator.CLR_ASSEMBLY + ".NameAttribute");
            bool isReserved = method.IsStatic && Emitter.IsReservedStaticName(method.Name) && !this.Validator.IsIgnoreType(method.DeclaringType);
            string name = method.Name;

            if (attr != null)
            {
                var value = attr.ConstructorArguments.First().Value;
                if (value is string)
                {
                    name = value.ToString();
                    if (isReserved)
                    {
                        name = "$" + name;
                    }
                    return name;
                }

                changeCase = (bool)value;
            }
            
            name = changeCase ? Ext.Net.Utilities.StringUtils.ToLowerCamelCase(name) : name;
            if (isReserved)
            {
                name = "$" + name;
            }

            return name;
        }

        protected virtual string GetEntityName(IEntity member, bool cancelChangeCase = false)
        {
            bool changeCase = !this.IsNativeMember(member.FullName) ? this.ChangeCase : true; 
            var attr = member.Attributes.FirstOrDefault(a => a.AttributeType.FullName == Translator.CLR_ASSEMBLY + ".NameAttribute");
            bool isReserved = member.IsStatic && Emitter.IsReservedStaticName(member.Name) && !this.Validator.IsIgnoreType(member.DeclaringTypeDefinition);
            string name = member.Name;

            if (attr != null) 
            {
                var value = attr.PositionalArguments.First().ConstantValue;
                if (value is string)
                {
                    name = value.ToString();
                    if (isReserved)
                    {
                        name = "$" + name;
                    }
                    return name;
                }

                changeCase = (bool)value;
            }
                        
            name = changeCase && !cancelChangeCase ? Ext.Net.Utilities.StringUtils.ToLowerCamelCase(name) : name;
            
            if (isReserved)
            {
                name = "$" + name;
            }

            return name;
        }

        protected virtual string GetEntityName(EntityDeclaration entity, bool cancelChangeCase = false)
        {
            bool changeCase = this.ChangeCase; 
            var attr = this.GetAttribute(entity.Attributes, Translator.CLR_ASSEMBLY + ".Name");
            
            string name = entity.Name;
            if (entity is FieldDeclaration)
            {
                name = this.GetFieldName((FieldDeclaration)entity);
            }
            else if (entity is EventDeclaration)
            {
                name = this.GetEventName((EventDeclaration)entity);
            }

            bool isReserved = entity.HasModifier(Modifiers.Static) && Emitter.IsReservedStaticName(name);
            

            if (attr != null)
            {
                var expr = (PrimitiveExpression)attr.Arguments.First();
                if (expr.Value is string)
                {
                    name = expr.Value.ToString();
                    if (isReserved)
                    {
                        name = "$" + name;
                    }
                    return name;
                }

                changeCase = (bool)expr.Value;
            }

            name = changeCase && !cancelChangeCase ? Ext.Net.Utilities.StringUtils.ToLowerCamelCase(name) : name;

            if (isReserved)
            {
                name = "$" + name;
            }

            return name;
        }

        protected virtual string GetFieldName(FieldDeclaration field)
        {
            if (!string.IsNullOrEmpty(field.Name))
            {
                return field.Name;
            }

            if (field.Variables.Count > 0)
            {
                return field.Variables.First().Name;
            }

            return null;
        }

        protected virtual string GetEventName(EventDeclaration evt)
        {
            if (!string.IsNullOrEmpty(evt.Name))
            {
                return evt.Name;
            }

            if (evt.Variables.Count > 0)
            {
                return evt.Variables.First().Name;
            }

            return null;
        }

        protected virtual string GetInline(ICustomAttributeProvider provider)
        {
            var attr = this.GetAttribute(provider.CustomAttributes, Translator.CLR_ASSEMBLY + ".InlineAttribute");
            
            return attr != null ? ((string)attr.ConstructorArguments.First().Value) : null;
        }
        
        protected virtual string GetInline(EntityDeclaration method)
        {
            var attr = this.GetAttribute(method.Attributes, Translator.CLR_ASSEMBLY + ".InlineAttribute");

            return attr != null ? ((string)((PrimitiveExpression)attr.Arguments.First()).Value) : null;
        }

        protected virtual string GetInline(IEntity entity)
        {
            string attrName = Translator.CLR_ASSEMBLY + ".InlineAttribute";

            if (entity.EntityType == EntityType.Property) 
            {
                var prop = (IProperty)entity;
                entity = this.IsAssignment ? prop.Setter : prop.Getter;
            }

            if (entity != null)
            {
                var attr = entity.Attributes.FirstOrDefault(a =>
                {
                    
                    return a.AttributeType.FullName == attrName;
                });
                
                return attr != null ? attr.PositionalArguments[0].ConstantValue.ToString() : null;
            }

            return null;
        }

        protected virtual IEnumerable<string> GetScriptArguments(ICSharpCode.NRefactory.CSharp.Attribute attr)
        {
            if (attr == null)
            {
                return null;
            }

            var result = new List<string>();

            foreach (var arg in attr.Arguments)
            {
                PrimitiveExpression expr = (PrimitiveExpression)arg;
                result.Add((string)expr.Value);
            }

            return result;
        }

        protected virtual void PushLocals()
        {
            if (this.LocalsStack == null)
            {
                this.LocalsStack = new Stack<Dictionary<string, AstType>>();
            }

            this.LocalsStack.Push(this.Locals);
            this.Locals = new Dictionary<string, AstType>(this.Locals);
        }

        protected virtual void PopLocals()
        {
            this.Locals = this.LocalsStack.Pop();
        }

        protected virtual void ResetLocals()
        {
            this.Locals = new Dictionary<string, AstType>();
            this.IteratorCount = 0;
        }

        protected virtual void AddLocals(IEnumerable<ParameterDeclaration> declarations)
        {
            declarations.ToList().ForEach(item => this.Locals.Add(item.Name, item.Type));
        }        

        protected virtual void EmitPropertyMethod(PropertyDeclaration propertyDeclaration, Accessor accessor, bool setter)
        {
            if (!accessor.IsNull && this.GetInline(accessor) == null)
            {
                this.EnsureComma();

                this.ResetLocals();

                if (setter)
                {
                    this.AddLocals(new ParameterDeclaration[] { new ParameterDeclaration { Name = "value" } });
                }

                this.Write((setter ? "set" : "get") + propertyDeclaration.Name);
                this.WriteColon();
                this.WriteFunction();
                this.WriteOpenParentheses();
                this.Write(setter ? "value" : "");
                this.WriteCloseParentheses();
                this.WriteSpace();

                var script = this.GetScript(accessor);

                if (script == null)
                {
                    if (!accessor.Body.IsNull)
                    {
                        accessor.Body.AcceptVisitor(this);
                    }
                    else
                    {
                        bool isReserved = propertyDeclaration.HasModifier(Modifiers.Static) && Emitter.IsReservedStaticName(propertyDeclaration.Name);
                        
                        this.BeginBlock();

                        if (setter)
                        {
                            this.Write("this." + (isReserved ? "$" : "") + propertyDeclaration.Name.ToLowerCamelCase() + " = value;");
                        }
                        else
                        {
                            this.WriteReturn(true);
                            this.Write("this." + (isReserved ? "$" : "") + propertyDeclaration.Name.ToLowerCamelCase());
                            this.WriteSemiColon();
                        }

                        this.WriteNewLine();
                        this.EndBlock();
                    }
                }
                else
                {
                    this.BeginBlock();

                    foreach (var line in script)
                    {
                        this.Write(line);
                        this.WriteNewLine();
                    }

                    this.EndBlock();
                }

                this.Comma = true;
            }
        }

        

        protected virtual PropertyDeclaration GetPropertyMember(MemberReferenceExpression memberReferenceExpression)
        {            
            bool isThis = memberReferenceExpression.Target is ThisReferenceExpression || memberReferenceExpression.Target is BaseReferenceExpression;
            string name = memberReferenceExpression.MemberName;

            if (isThis) 
            {
                IDictionary<string, PropertyDeclaration> dict = this.TypeInfo.InstanceProperties;
                return dict.ContainsKey(name) ? dict[name] : null;
            }

            IdentifierExpression expr = memberReferenceExpression.Target as IdentifierExpression;

            if (expr != null)
            {
                if (this.Locals.ContainsKey(expr.Identifier)) 
                {
                    var type = this.Locals[expr.Identifier];
                    string resolved = this.ResolveType(type.ToString());

                    if(!string.IsNullOrEmpty(resolved)) 
                    {
                        var typeInfo = this.Types.FirstOrDefault(t => t.FullName == resolved);

                        if (typeInfo != null)
                        {
                            if (typeInfo.InstanceProperties.ContainsKey(name))
                            {
                                return typeInfo.InstanceProperties[name];
                            }

                            if (typeInfo.StaticProperties.ContainsKey(name))
                            {
                                return typeInfo.StaticProperties[name];
                            }
                        }                        
                    }
                }
                else
                {
                    IMemberDefinition member = this.ResolveFieldOrMethod(expr.Identifier, 0);

                    if (member != null && member is FieldDefinition)
                    {
                        FieldDefinition field = member as FieldDefinition;
                        string resolved = this.ResolveType(field.FieldType.Name);

                        if (!string.IsNullOrEmpty(resolved))
                        {
                            var typeInfo = this.Types.FirstOrDefault(t => t.FullName == resolved);

                            if (typeInfo != null)
                            {
                                if (typeInfo.InstanceProperties.ContainsKey(name))
                                {
                                    return typeInfo.InstanceProperties[name];
                                }

                                if (typeInfo.StaticProperties.ContainsKey(name))
                                {
                                    return typeInfo.StaticProperties[name];
                                }
                            }                        
                        }
                    }
                    else
                    {
                        string resolved = this.ResolveType(expr.Identifier);

                        if (!string.IsNullOrEmpty(resolved))
                        {
                            var typeInfo = this.Types.FirstOrDefault(t => t.FullName == resolved);

                            if (typeInfo != null)
                            {
                                if (typeInfo.InstanceProperties.ContainsKey(name))
                                {
                                    return typeInfo.InstanceProperties[name];
                                }

                                if (typeInfo.StaticProperties.ContainsKey(name))
                                {
                                    return typeInfo.StaticProperties[name];
                                }
                            }
                            else if (this.TypeDefinitions.ContainsKey(resolved))
                            {
                                TypeDefinition typeDef = this.TypeDefinitions[resolved];
                                PropertyDefinition propDef = typeDef.Properties.FirstOrDefault(p => p.Name == name);

                                if (propDef != null)
                                {
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }        

        protected virtual void EmitTypeReference(AstType astType)
        {
            var composedType = astType as ComposedType;

            if (composedType != null && composedType.ArraySpecifiers != null && composedType.ArraySpecifiers.Count > 0)
            {
                this.Write("Array");
            }
            else
            {
                string type = this.ResolveType(Helpers.GetScriptName(astType, true));

                if (String.IsNullOrEmpty(type))
                {
                    throw CreateException(astType, "Cannot resolve type " + astType.ToString());
                }

                this.Write(Ext.Net.Utilities.StringUtils.LeftOfRightmostOf(this.ShortenTypeName(type), "$"));
            }
        }

        protected virtual void PushWriter(string format)
        {
            this.Writers.Push(new Tuple<string, StringBuilder, bool>(format, this.Output, this.IsNewLine));
            this.IsNewLine = false;
            this.Output = new StringBuilder();
        }

        protected virtual string PopWriter(bool preventWrite = false)
        {
            string result = this.Output.ToString();
            var tuple = this.Writers.Pop();
            this.Output = tuple.Item2;
            result = tuple.Item1 != null ? string.Format(tuple.Item1, result) : result;
            this.IsNewLine = tuple.Item3;
            if (!preventWrite)
            {                
                this.Write(result);
            }

            return result;
        }

        protected virtual bool IsNativeMember(string fullName)
        {
            return fullName.Contains(Translator.CLR_ASSEMBLY) || fullName.StartsWith("System");
        }

        protected virtual bool IsMemberConst(IMember member)
        {
            return (member is DefaultResolvedField) && (((DefaultResolvedField)member).IsConst && member.DeclaringType.Kind != TypeKind.Enum);
        }

        protected virtual bool IsInlineConst(IMember member)
        {            
            bool isConst = (member is DefaultResolvedField) && (((DefaultResolvedField)member).IsConst && member.DeclaringType.Kind != TypeKind.Enum);

            if (isConst)
            {
                var attr = this.GetAttribute(member.Attributes, Translator.CLR_ASSEMBLY + ".InlineConstAttribute");

                if (attr != null)
                {
                    return true;
                }
            }

            return false;
        }

        protected virtual void LogWarning(string message)
        {
            this.LogMessage("warning", message);
        }

        protected virtual void LogError(string message)
        {
            this.LogMessage("error", message);
        }

        protected virtual void LogMessage(string message)
        {
            this.LogMessage("message", message);
        }

        protected virtual void LogMessage(string level, string message)
        {
            if (this.Log != null)
            {
                this.Log(level, message);
            }
        }
    }
}
