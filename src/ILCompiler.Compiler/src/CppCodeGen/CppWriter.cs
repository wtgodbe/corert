// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ILCompiler.Compiler.CppCodeGen;
using ILCompiler.DependencyAnalysisFramework;

using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

using Internal.Runtime;

using Internal.IL;
using ILCompiler.DependencyAnalysis;

namespace ILCompiler.CppCodeGen
{
    internal class CppWriter
    {
        private CppCodegenCompilation _compilation;

        private void SetWellKnownTypeSignatureName(WellKnownType wellKnownType, string mangledSignatureName)
        {
            var type = _compilation.TypeSystemContext.GetWellKnownType(wellKnownType);
            var typeNode = _compilation.NodeFactory.ConstructedTypeSymbol(type);

            _cppSignatureNames.Add(type, mangledSignatureName);
        }

        public CppWriter(CppCodegenCompilation compilation, string outputFilePath)
        {
            _compilation = compilation;

            _out = new StreamWriter(new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, false));


            // Unify this list with the one in CppCodegenNodeFactory
            SetWellKnownTypeSignatureName(WellKnownType.Void, "void");
            SetWellKnownTypeSignatureName(WellKnownType.Boolean, "uint8_t");
            SetWellKnownTypeSignatureName(WellKnownType.Char, "uint16_t");
            SetWellKnownTypeSignatureName(WellKnownType.SByte, "int8_t");
            SetWellKnownTypeSignatureName(WellKnownType.Byte, "uint8_t");
            SetWellKnownTypeSignatureName(WellKnownType.Int16, "int16_t");
            SetWellKnownTypeSignatureName(WellKnownType.UInt16, "uint16_t");
            SetWellKnownTypeSignatureName(WellKnownType.Int32, "int32_t");
            SetWellKnownTypeSignatureName(WellKnownType.UInt32, "uint32_t");
            SetWellKnownTypeSignatureName(WellKnownType.Int64, "int64_t");
            SetWellKnownTypeSignatureName(WellKnownType.UInt64, "uint64_t");
            SetWellKnownTypeSignatureName(WellKnownType.IntPtr, "intptr_t");
            SetWellKnownTypeSignatureName(WellKnownType.UIntPtr, "uintptr_t");
            SetWellKnownTypeSignatureName(WellKnownType.Single, "float");
            SetWellKnownTypeSignatureName(WellKnownType.Double, "double");

            BuildExternCSignatureMap();
        }

        private Dictionary<TypeDesc, string> _cppSignatureNames = new Dictionary<TypeDesc, string>();

        public string GetCppSignatureTypeName(TypeDesc type)
        {
            string mangledName;
            if (_cppSignatureNames.TryGetValue(type, out mangledName))
                return mangledName;

            // TODO: Use friendly names for enums
            if (type.IsEnum)
                mangledName = GetCppSignatureTypeName(type.UnderlyingType);
            else
                mangledName = GetCppTypeName(type);

            if (!type.IsValueType && !type.IsByRef && !type.IsPointer)
                mangledName += "*";

            _cppSignatureNames.Add(type, mangledName);

            return mangledName;
        }

        // extern "C" methods are sometimes referenced via different signatures.
        // _externCSignatureMap contains the canonical signature of the extern "C" import. References
        // via other signatures are required to use casts.
        private Dictionary<string, MethodSignature> _externCSignatureMap = new Dictionary<string, MethodSignature>();

        private void BuildExternCSignatureMap()
        {
            foreach (var nodeAlias in _compilation.NodeFactory.NodeAliases)
            {
                var methodNode = (CppMethodCodeNode)nodeAlias.Key;
                _externCSignatureMap.Add(nodeAlias.Value, methodNode.Method.Signature);
            }
        }

        private IEnumerable<string> GetParameterNamesForMethod(MethodDesc method)
        {
            // TODO: The uses of this method need revision. The right way to get to this info is from
            //       a MethodIL. For declarations, we don't need names.

            method = method.GetTypicalMethodDefinition();
            var ecmaMethod = method as EcmaMethod;
            if (ecmaMethod != null && ecmaMethod.Module.PdbReader != null)
            {
                return (new EcmaMethodDebugInformation(ecmaMethod)).GetParameterNames();
            }

            return null;
        }

        public void AppendCppMethodDeclaration(CppGenerationBuffer sb, MethodDesc method, bool implementation, string externalMethodName = null, MethodSignature methodSignature = null)
        {
            if (methodSignature == null)
                methodSignature = method.Signature;

            if (externalMethodName != null)
            {
                sb.Append("extern \"C\" ");
            }
            else
            {
                if (!implementation)
                {
                    sb.Append("static ");
                }
            }
            sb.Append(GetCppSignatureTypeName(methodSignature.ReturnType));
            sb.Append(" ");
            if (externalMethodName != null)
            {
                sb.Append(externalMethodName);
            }
            else
            {
                if (implementation)
                {
                    sb.Append(GetCppMethodDeclarationName(method.OwningType, GetCppMethodName(method)));
                }
                else
                {
                    sb.Append(GetCppMethodName(method));
                }
            }
            sb.Append("(");
            bool hasThis = !methodSignature.IsStatic;
            int argCount = methodSignature.Length;
            if (hasThis)
                argCount++;

            List<string> parameterNames = null;
            if (method != null)
            {
                IEnumerable<string> parameters = GetParameterNamesForMethod(method);
                if (parameters != null)
                {
                    parameterNames = new List<string>(parameters);
                    if (parameterNames.Count != 0)
                    {
                        System.Diagnostics.Debug.Assert(parameterNames.Count == argCount);
                    }
                    else
                    {
                        parameterNames = null;
                    }
                }
            }

            for (int i = 0; i < argCount; i++)
            {
                if (hasThis)
                {
                    if (i == 0)
                    {
                        var thisType = method.OwningType;
                        if (thisType.IsValueType)
                            thisType = thisType.MakeByRefType();
                        sb.Append(GetCppSignatureTypeName(thisType));
                    }
                    else
                    {
                        sb.Append(GetCppSignatureTypeName(methodSignature[i - 1]));
                    }
                }
                else
                {
                    sb.Append(GetCppSignatureTypeName(methodSignature[i]));
                }
                if (implementation)
                {
                    sb.Append(" ");

                    if (parameterNames != null)
                    {
                        sb.Append(SanitizeCppVarName(parameterNames[i]));
                    }
                    else
                    {
                        sb.Append("_a");
                        sb.Append(i.ToStringInvariant());
                    }
                }
                if (i != argCount - 1)
                    sb.Append(", ");
            }
            sb.Append(")");
            if (!implementation)
                sb.Append(";");
        }

        public void AppendCppMethodCallParamList(CppGenerationBuffer sb, MethodDesc method)
        {
            var methodSignature = method.Signature;

            bool hasThis = !methodSignature.IsStatic;
            int argCount = methodSignature.Length;
            if (hasThis)
                argCount++;

            List<string> parameterNames = null;
            IEnumerable<string> parameters = GetParameterNamesForMethod(method);
            if (parameters != null)
            {
                parameterNames = new List<string>(parameters);
                if (parameterNames.Count != 0)
                {
                    System.Diagnostics.Debug.Assert(parameterNames.Count == argCount);
                }
                else
                {
                    parameterNames = null;
                }
            }

            for (int i = 0; i < argCount; i++)
            {
                if (parameterNames != null)
                {
                    sb.Append(SanitizeCppVarName(parameterNames[i]));
                }
                else
                {
                    sb.Append("_a");
                    sb.Append(i.ToStringInvariant());
                }
                if (i != argCount - 1)
                    sb.Append(", ");
            }
        }

        public string GetCppTypeName(TypeDesc type)
        {
            switch (type.Category)
            {
                case TypeFlags.ByRef:
                case TypeFlags.Pointer:
                    return GetCppSignatureTypeName(((ParameterizedType)type).ParameterType) + "*";
                default:
                    return _compilation.NameMangler.GetMangledTypeName(type);
            }
        }

        /// <summary>
        /// Compute a proper declaration for <param name="methodName"/> defined in <param name="owningType"/>.
        /// Usually the C++ name for a type is prefixed by "::" but this is not a valid way to declare a method,
        /// so we need to strip it if present.
        /// </summary>
        /// <param name="owningType">Type where <param name="methodName"/> belongs.</param>
        /// <param name="methodName">Name of method from <param name="owningType"/>.</param>
        /// <returns>C++ declaration name for <param name="methodName"/>.</returns>
        public string GetCppMethodDeclarationName(TypeDesc owningType, string methodName)
        {
            var s = GetCppTypeName(owningType);
            if (s.StartsWith("::"))
            {
                // For a Method declaration we do not need the starting ::
                s = s.Substring(2, s.Length - 2);
            }
            return string.Concat(s, "::", methodName);
        }

        public string GetCppMethodName(MethodDesc method)
        {
            return _compilation.NameMangler.GetMangledMethodName(method);
        }

        public string GetCppFieldName(FieldDesc field)
        {
            return _compilation.NameMangler.GetMangledFieldName(field);
        }

        public string GetCppStaticFieldName(FieldDesc field)
        {
            TypeDesc type = field.OwningType;
            string typeName = GetCppTypeName(type);
            return typeName.Replace("::", "__") + "__" + _compilation.NameMangler.GetMangledFieldName(field);
        }

        public string SanitizeCppVarName(string varName)
        {
            // TODO: name mangling robustness
            if (varName == "errno") // some names collide with CRT headers
                varName += "_";

            return varName;
        }

        private void CompileExternMethod(CppMethodCodeNode methodCodeNodeNeedingCode, string importName)
        {
            MethodDesc method = methodCodeNodeNeedingCode.Method;
            MethodSignature methodSignature = method.Signature;

            bool slotCastRequired = false;

            MethodSignature externCSignature;
            if (_externCSignatureMap.TryGetValue(importName, out externCSignature))
            {
                slotCastRequired = !externCSignature.Equals(methodSignature);
            }
            else
            {
                _externCSignatureMap.Add(importName, methodSignature);
                externCSignature = methodSignature;
            }

            var sb = new CppGenerationBuffer();

            sb.AppendLine();
            AppendCppMethodDeclaration(sb, method, true);
            sb.AppendLine();
            sb.Append("{");
            sb.Indent();

            if (slotCastRequired)
            {
                AppendSlotTypeDef(sb, method);
            }

            sb.AppendLine();
            if (!method.Signature.ReturnType.IsVoid)
            {
                sb.Append("return ");
            }

            if (slotCastRequired)
                sb.Append("((__slot__" + GetCppMethodName(method) + ")");
            sb.Append("::");
            sb.Append(importName);
            if (slotCastRequired)
                sb.Append(")");

            sb.Append("(");
            AppendCppMethodCallParamList(sb, method);
            sb.Append(");");
            sb.Exdent();
            sb.AppendLine();
            sb.Append("}");

            methodCodeNodeNeedingCode.SetCode(sb.ToString(), Array.Empty<Object>());
        }

        public void CompileMethod(CppMethodCodeNode methodCodeNodeNeedingCode)
        {
            MethodDesc method = methodCodeNodeNeedingCode.Method;

            _compilation.Logger.Writer.WriteLine("Compiling " + method.ToString());
            if (method.HasCustomAttribute("System.Runtime", "RuntimeImportAttribute"))
            {
                CompileExternMethod(methodCodeNodeNeedingCode, ((EcmaMethod)method).GetRuntimeImportName());
                return;
            }

            if (method.IsRawPInvoke())
            {
                CompileExternMethod(methodCodeNodeNeedingCode, method.GetPInvokeMethodMetadata().Name ?? method.Name);
                return;
            }

            var methodIL = _compilation.GetMethodIL(method);
            if (methodIL == null)
                return;

            // TODO: Remove this code once CppCodegen is able to generate code for the reflection startup path.
            //       The startup path runs before any user code is executed.
            //       For now we replace the startup path with a simple "ret". Reflection won't work, but
            //       programs not using reflection will.
            if (method.Name == ".cctor")
            {
                MetadataType owningType = method.OwningType as MetadataType;
                if (owningType != null &&
                    owningType.Name == "ReflectionExecution" && owningType.Namespace == "Internal.Reflection.Execution")
                {
                    methodIL = new Internal.IL.Stubs.ILStubMethodIL(method, new byte[] { (byte)ILOpcode.ret }, Array.Empty<LocalVariableDefinition>(), null);
                }
            }

            try
            {
                // TODO: hacky special-case
                if (method.Name == "_ecvt_s")
                    throw new NotImplementedException();

                var ilImporter = new ILImporter(_compilation, this, method, methodIL);

                CompilerTypeSystemContext typeSystemContext = _compilation.TypeSystemContext;

                MethodDebugInformation debugInfo = _compilation.GetDebugInfo(methodIL);

                if (!_compilation.Options.HasOption(CppCodegenConfigProvider.NoLineNumbersString))
                {
                    IEnumerable<ILSequencePoint> sequencePoints = debugInfo.GetSequencePoints();
                    if (sequencePoints != null)
                        ilImporter.SetSequencePoints(sequencePoints);
                }

                IEnumerable<ILLocalVariable> localVariables = debugInfo.GetLocalVariables();
                if (localVariables != null)
                    ilImporter.SetLocalVariables(localVariables);

                IEnumerable<string> parameters = GetParameterNamesForMethod(method);
                if (parameters != null)
                    ilImporter.SetParameterNames(parameters);

                ilImporter.Compile(methodCodeNodeNeedingCode);
            }
            catch (Exception e)
            {
                _compilation.Logger.Writer.WriteLine(e.Message + " (" + method + ")");

                var sb = new CppGenerationBuffer();
                sb.AppendLine();
                AppendCppMethodDeclaration(sb, method, true);
                sb.AppendLine();
                sb.Append("{");
                sb.Indent();
                sb.AppendLine();
                sb.Append("throw 0xC000C000;");
                sb.Exdent();
                sb.AppendLine();
                sb.Append("}");

                methodCodeNodeNeedingCode.SetCode(sb.ToString(), Array.Empty<Object>());
            }
        }

        private TextWriter Out
        {
            get
            {
                return _out;
            }
        }

        private StreamWriter _out;

        private Dictionary<TypeDesc, List<MethodDesc>> _methodLists;

        private CppGenerationBuffer _statics;
        private CppGenerationBuffer _gcStatics;
        private CppGenerationBuffer _threadStatics;
        private CppGenerationBuffer _gcThreadStatics;

        // Base classes and valuetypes has to be emitted before they are used.
        private HashSet<TypeDesc> _emittedTypes;

        private TypeDesc GetFieldTypeOrPlaceholder(FieldDesc field)
        {
            try
            {
                return field.FieldType;
            }
            catch
            {
                // TODO: For now, catch errors due to missing dependencies
                return _compilation.TypeSystemContext.GetWellKnownType(WellKnownType.Boolean);
            }
        }

        private void OutputTypeFields(CppGenerationBuffer sb, TypeDesc t)
        {
            bool explicitLayout = false;
            ClassLayoutMetadata classLayoutMetadata = default(ClassLayoutMetadata);

            if (t.IsValueType)
            {
                MetadataType metadataType = (MetadataType)t;
                if (metadataType.IsExplicitLayout)
                {
                    explicitLayout = true;
                    classLayoutMetadata = metadataType.GetClassLayout();
                }
            }

            int instanceFieldIndex = 0;

            if (explicitLayout)
            {
                sb.AppendLine();
                sb.Append("union {");
                sb.Indent();
            }

            foreach (var field in t.GetFields())
            {
                if (field.IsStatic)
                {
                    if (field.IsLiteral)
                        continue;

                    TypeDesc fieldType = GetFieldTypeOrPlaceholder(field);
                    CppGenerationBuffer builder;
                    if (!fieldType.IsValueType)
                    {
                        builder = _gcStatics;
                    }
                    else
                    {
                        // TODO: Valuetype statics with GC references
                        builder = _statics;
                    }
                    builder.AppendLine();
                    builder.Append(GetCppSignatureTypeName(fieldType));
                    builder.Append(" ");
                    builder.Append(GetCppStaticFieldName(field) + ";");
                }
                else
                {
                    if (explicitLayout)
                    {
                        sb.AppendLine();
                        sb.Append("struct {");
                        sb.Indent();
                        int offset = classLayoutMetadata.Offsets[instanceFieldIndex].Offset;
                        if (offset > 0)
                        {
                            sb.AppendLine();
                            sb.Append("char __pad" + instanceFieldIndex + "[" + offset + "];");
                        }
                    }
                    sb.AppendLine();
                    sb.Append(GetCppSignatureTypeName(GetFieldTypeOrPlaceholder(field)) + " " + GetCppFieldName(field) + ";");
                    if (explicitLayout)
                    {
                        sb.Exdent();
                        sb.AppendLine();
                        sb.Append("};");
                    }
                    instanceFieldIndex++;
                }
            }

            if (explicitLayout)
            {
                sb.Exdent();
                sb.AppendLine();
                sb.Append("};");
            }
        }

        private void AppendSlotTypeDef(CppGenerationBuffer sb, MethodDesc method)
        {
            MethodSignature methodSignature = method.Signature;

            TypeDesc thisArgument = null;
            if (!methodSignature.IsStatic)
                thisArgument = method.OwningType;

            AppendSignatureTypeDef(sb, "__slot__" + GetCppMethodName(method), methodSignature, thisArgument);
        }

        internal void AppendSignatureTypeDef(CppGenerationBuffer sb, string name, MethodSignature methodSignature, TypeDesc thisArgument)
        {
            sb.AppendLine();
            sb.Append("typedef ");
            sb.Append(GetCppSignatureTypeName(methodSignature.ReturnType));
            sb.Append("(*");
            sb.Append(name);
            sb.Append(")(");

            int argCount = methodSignature.Length;
            if (thisArgument != null)
                argCount++;
            for (int i = 0; i < argCount; i++)
            {
                if (thisArgument != null)
                {
                    if (i == 0)
                    {
                        sb.Append(GetCppSignatureTypeName(thisArgument));
                    }
                    else
                    {
                        sb.Append(GetCppSignatureTypeName(methodSignature[i - 1]));
                    }
                }
                else
                {
                    sb.Append(GetCppSignatureTypeName(methodSignature[i]));
                }
                if (i != argCount - 1)
                    sb.Append(", ");
            }
            sb.Append(");");
        }

        private String GetCodeForDelegate(TypeDesc delegateType)
        {
            var sb = new CppGenerationBuffer();

            MethodDesc method = delegateType.GetKnownMethod("Invoke", null);

            AppendSlotTypeDef(sb, method);

            sb.AppendLine();
            sb.Append("static __slot__");
            sb.Append(GetCppMethodName(method));
            sb.Append(" __invoke__");
            sb.Append(GetCppMethodName(method));
            sb.Append("(void * pThis)");
            sb.AppendLine();
            sb.Append("{");
            sb.Indent();
            sb.AppendLine();
            sb.Append("return (__slot__");
            sb.Append(GetCppMethodName(method));
            sb.Append(")(((");
            sb.Append(GetCppSignatureTypeName(_compilation.TypeSystemContext.GetWellKnownType(WellKnownType.MulticastDelegate)));
            sb.Append(")pThis)->m_functionPointer);");
            sb.Exdent();
            sb.AppendLine();
            sb.Append("};");

            return sb.ToString();
        }

        private String GetCodeForVirtualMethod(MethodDesc method, int slot)
        {
            var sb = new CppGenerationBuffer();
            sb.Indent();

            if (method.OwningType.IsInterface)
            {

                AppendSlotTypeDef(sb, method);
                sb.Indent();
                sb.AppendLine();
                sb.Append("static uint16_t");
                sb.Append(" __getslot__");
                sb.Append(GetCppMethodName(method));
                sb.Append("(void * pThis)");
                sb.AppendLine();
                sb.Append("{");
                sb.Indent();
                sb.AppendLine();
                sb.Append("return ");
                sb.Append(slot);
                sb.Append(";");
                sb.AppendLine();
            }
            else
            {
                AppendSlotTypeDef(sb, method);
                sb.Indent();
                sb.AppendLine();
                sb.Append("static __slot__");
                sb.Append(GetCppMethodName(method));
                sb.Append(" __getslot__");
                sb.Append(GetCppMethodName(method));
                sb.Append("(void * pThis)");
                sb.AppendLine();
                sb.Append("{");
                sb.Indent();
                sb.AppendLine();
                sb.Append(" return (__slot__");
                sb.Append(GetCppMethodName(method));
                sb.Append(")*((void **)(*((RawEEType **)pThis) + 1) + ");
                sb.Append(slot.ToStringInvariant());
                sb.Append(");");

            }
            sb.Exdent();
            sb.AppendLine();
            sb.Append("};");
            sb.Exdent();
            return sb.ToString();
        }

        private String GetCodeForObjectNode(ObjectNode node, NodeFactory factory)
        {
            // virtual slots
            var nodeData = node.GetData(factory, false);

            CppGenerationBuffer nodeCode = new CppGenerationBuffer();

            /* Create list of byte data. Used to divide contents between reloc and byte data
          * First val - isReloc
          * Second val - size of byte data if first value of tuple is false
          */

            List<NodeDataSection> nodeDataSections = new List<NodeDataSection>();
            byte[] actualData = new byte[nodeData.Data.Length];
            Relocation[] relocs = nodeData.Relocs;

            int nextRelocOffset = -1;
            int nextRelocIndex = -1;
            int lastByteIndex = 0;

            if (relocs.Length > 0)
            {
                nextRelocOffset = relocs[0].Offset;
                nextRelocIndex = 0;
            }

            int i = 0;
            int offset = 0;
            CppGenerationBuffer nodeDataDecl = new CppGenerationBuffer();

            if (node is ISymbolNode)
            {
                offset = (node as ISymbolNode).Offset;
                i = offset;
                lastByteIndex = offset;
            }
            while (i < nodeData.Data.Length)
            {
                if (i == nextRelocOffset)
                {
                    Relocation reloc = relocs[nextRelocIndex];

                    int size = _compilation.TypeSystemContext.Target.PointerSize;
                    // Make sure we've gotten the correct size for the reloc
                    System.Diagnostics.Debug.Assert(reloc.RelocType == (size == 8 ? RelocType.IMAGE_REL_BASED_DIR64 : RelocType.IMAGE_REL_BASED_HIGHLOW));

                    // Update nextRelocIndex/Offset
                    if (++nextRelocIndex < relocs.Length)
                    {
                        nextRelocOffset = relocs[nextRelocIndex].Offset;
                    }
                    nodeDataSections.Add(new NodeDataSection(NodeDataSectionType.Relocation, size));
                    i += size;
                    lastByteIndex = i;
                }
                else
                {
                    i++;
                    if (i + 1 == nextRelocOffset || i + 1 == nodeData.Data.Length)
                    {
                        nodeDataSections.Add(new NodeDataSection(NodeDataSectionType.ByteData, (i + 1) - lastByteIndex));
                    }
                }
            }
            string pointerType = node is EETypeNode ? "MethodTable * " : "void* ";
            nodeCode.Append(pointerType);
            if (node is EETypeNode)
            {
                nodeCode.Append(GetCppMethodDeclarationName((node as EETypeNode).Type, "__getMethodTable"));
            }
            else
            {
                string mangledName = ((ISymbolNode)node).MangledName;

                // Rename generic composition and optional fields nodes to avoid name clash with types
                bool shouldReplaceNamespaceQualifier = node is GenericCompositionNode || node is EETypeOptionalFieldsNode;
                nodeCode.Append(shouldReplaceNamespaceQualifier ? mangledName.Replace("::", "_") : mangledName);
            }
            nodeCode.Append("()");
            nodeCode.AppendLine();
            nodeCode.Append("{");
            nodeCode.Indent();
            nodeCode.AppendLine();
            nodeCode.Append("static struct {");

            nodeCode.AppendLine();
            nodeCode.Append(GetCodeForNodeStruct(nodeDataSections, node));

            nodeCode.AppendLine();
            nodeCode.Append("} mt = {");
            nodeCode.Append(GetCodeForNodeData(nodeDataSections, relocs, nodeData.Data, node, offset, factory));

            nodeCode.Append("};");
            nodeCode.AppendLine();
            nodeCode.Append("return ( ");
            nodeCode.Append(pointerType);
            nodeCode.Append(")&mt;");
            nodeCode.Exdent();
            nodeCode.AppendLine();
            nodeCode.Append("}");
            nodeCode.AppendLine();
            return nodeCode.ToString();
        }

        private String GetCodeForNodeData(List<NodeDataSection> nodeDataSections, Relocation[] relocs, byte[] byteData, DependencyNode node, int offset, NodeFactory factory)
        {
            CppGenerationBuffer nodeDataDecl = new CppGenerationBuffer();
            int relocCounter = 0;
            int divisionStartIndex = offset;
            nodeDataDecl.Indent();
            nodeDataDecl.Indent();
            nodeDataDecl.AppendLine();

            for (int i = 0; i < nodeDataSections.Count; i++)
            {
                if (nodeDataSections[i].SectionType == NodeDataSectionType.Relocation)
                {
                    Relocation reloc = relocs[relocCounter];
                    nodeDataDecl.Append(GetCodeForReloc(reloc, node, factory));
                    nodeDataDecl.Append(",");
                    relocCounter++;
                }
                else
                {
                    AppendFormattedByteArray(nodeDataDecl, byteData, divisionStartIndex, divisionStartIndex + nodeDataSections[i].SectionSize);
                    nodeDataDecl.Append(",");
                }
                divisionStartIndex += nodeDataSections[i].SectionSize;
                nodeDataDecl.AppendLine();
            }
            return nodeDataDecl.ToString();
        }

        private String GetCodeForReloc(Relocation reloc, DependencyNode node, NodeFactory factory)
        {
            CppGenerationBuffer relocCode = new CppGenerationBuffer();
            if (reloc.Target is CppMethodCodeNode)
            {
                var method = reloc.Target as CppMethodCodeNode;

                relocCode.Append("(void*)&");
                relocCode.Append(GetCppMethodDeclarationName(method.Method.OwningType, GetCppMethodName(method.Method)));
            }
            else if (reloc.Target is EETypeNode && node is EETypeNode)
            {
                relocCode.Append(GetCppMethodDeclarationName((reloc.Target as EETypeNode).Type, "__getMethodTable"));
                relocCode.Append("()");
            }
            // Node is either an non-emitted type or a generic composition - both are ignored for CPP codegen
            else if ((reloc.Target is TypeManagerIndirectionNode || reloc.Target is InterfaceDispatchMapNode || reloc.Target is EETypeOptionalFieldsNode || reloc.Target is GenericCompositionNode) && !(reloc.Target as ObjectNode).ShouldSkipEmittingObjectNode(factory))
            {
                bool shouldReplaceNamespaceQualifier = reloc.Target is GenericCompositionNode || reloc.Target is EETypeOptionalFieldsNode;
                relocCode.Append(shouldReplaceNamespaceQualifier ? reloc.Target.MangledName.Replace("::", "_") : reloc.Target.MangledName);
                relocCode.Append("()");
            }
            else if (reloc.Target is ObjectAndOffsetSymbolNode && (reloc.Target as ISymbolNode).MangledName.Contains("DispatchMap"))
            {
                relocCode.Append("dispatchMapModule");
            }
            else
            {
                relocCode.Append("NULL");
            }
            return relocCode.ToString();
        }

        private String GetCodeForNodeStruct(List<NodeDataSection> nodeDataDivs, DependencyNode node)
        {
            CppGenerationBuffer nodeStructDecl = new CppGenerationBuffer();
            int relocCounter = 1;
            int i = 0;
            nodeStructDecl.Indent();

            for (i = 0; i < nodeDataDivs.Count; i++)
            {
                NodeDataSection section = nodeDataDivs[i];
                if (section.SectionType == NodeDataSectionType.Relocation)
                {
                    nodeStructDecl.Append("void* reloc");
                    nodeStructDecl.Append(relocCounter);
                    nodeStructDecl.Append(";");
                    relocCounter++;
                }
                else
                {
                    nodeStructDecl.Append("unsigned char data");
                    nodeStructDecl.Append((i + 1) - relocCounter);
                    nodeStructDecl.Append("[");
                    nodeStructDecl.Append(section.SectionSize);
                    nodeStructDecl.Append("];");
                }
                nodeStructDecl.AppendLine();
            }
            nodeStructDecl.Exdent();

            return nodeStructDecl.ToString();
        }

        private static void AppendFormattedByteArray(CppGenerationBuffer sb, byte[] array, int startIndex, int endIndex)
        {
            sb.Append("{");
            sb.Append("0x");
            sb.Append(BitConverter.ToString(array, startIndex, endIndex - startIndex).Replace("-", ",0x"));
            sb.Append("}");
        }

        private void BuildMethodLists(IEnumerable<DependencyNode> nodes)
        {
            _methodLists = new Dictionary<TypeDesc, List<MethodDesc>>();
            foreach (var node in nodes)
            {
                if (node is CppMethodCodeNode)
                {
                    CppMethodCodeNode methodCodeNode = (CppMethodCodeNode)node;

                    var method = methodCodeNode.Method;
                    var type = method.OwningType;

                    List<MethodDesc> methodList;
                    if (!_methodLists.TryGetValue(type, out methodList))
                    {
                        GetCppSignatureTypeName(type);

                        methodList = new List<MethodDesc>();
                        _methodLists.Add(type, methodList);
                    }

                    methodList.Add(method);
                }
                else
                if (node is IEETypeNode)
                {
                    IEETypeNode eeTypeNode = (IEETypeNode)node;

                    if (eeTypeNode.Type.IsGenericDefinition)
                    {
                        // TODO: CppWriter can't handle generic type definition EETypes
                    }
                    else
                        GetCppSignatureTypeName(eeTypeNode.Type);
                }
            }
        }

        /// <summary>
        /// Output C++ code via a given dependency graph
        /// </summary>
        /// <param name="nodes">A set of dependency nodes</param>
        /// <param name="entrypoint">Code entrypoint</param>
        /// <param name="factory">Associated NodeFactory instance</param>
        /// <param name="definitions">Text buffer in which the type and method definitions will be written</param>
        /// <param name="implementation">Text buffer in which the method implementations will be written</param>
        public void OutputNodes(IEnumerable<DependencyNode> nodes, NodeFactory factory)
        {
            CppGenerationBuffer dispatchPointers = new CppGenerationBuffer();
            CppGenerationBuffer forwardDefinitions = new CppGenerationBuffer();
            CppGenerationBuffer typeDefinitions = new CppGenerationBuffer();
            CppGenerationBuffer methodTables = new CppGenerationBuffer();
            CppGenerationBuffer additionalNodes = new CppGenerationBuffer();
            DependencyNodeIterator nodeIterator = new DependencyNodeIterator(nodes);

            // Number of InterfaceDispatchMapNodes needs to be declared explicitly for Ubuntu and OSX
            int dispatchMapCount = 0;
            dispatchPointers.AppendLine();
            dispatchPointers.Indent();

            //RTR header needs to be declared after all modules have already been output
            string rtrHeader = string.Empty;

            // Iterate through nodes
            foreach (var node in nodeIterator.GetNodes())
            {
                if (node is EETypeNode)
                    OutputTypeNode(node as EETypeNode, factory, forwardDefinitions, typeDefinitions, methodTables);
                else if ((node is EETypeOptionalFieldsNode || node is TypeManagerIndirectionNode || node is GenericCompositionNode) && !(node as ObjectNode).ShouldSkipEmittingObjectNode(factory))
                    additionalNodes.Append(GetCodeForObjectNode(node as ObjectNode, factory));
                else if (node is InterfaceDispatchMapNode)
                {
                    dispatchPointers.Append("(void *)");
                    dispatchPointers.Append(((ISymbolNode)node).MangledName);
                    dispatchPointers.Append("(),");
                    dispatchPointers.AppendLine();
                    dispatchMapCount++;
                    additionalNodes.Append(GetCodeForObjectNode(node as ObjectNode, factory));

                }
                else if (node is ReadyToRunHeaderNode)
                    rtrHeader = GetCodeForReadyToRunHeader(node as ReadyToRunHeaderNode, factory);
            }

            dispatchPointers.AppendLine();
            dispatchPointers.Exdent();

            Out.Write(forwardDefinitions.ToString());

            Out.Write(typeDefinitions.ToString());

            Out.Write(additionalNodes.ToString());

            Out.Write(methodTables.ToString());

            // Emit pointers to dispatch map nodes, to be used in interface dispatch
            Out.Write("void * dispatchMapModule[");
            Out.Write(dispatchMapCount);
            Out.Write("] = {");
            Out.Write(dispatchPointers.ToString());
            Out.Write("};");

            Out.Write(rtrHeader);
        }

        /// <summary>
        /// Output C++ code for a given codeNode
        /// </summary>
        /// <param name="methodCodeNode">The code node to be output</param>
        /// <param name="methodImplementations">The buffer in which to write out the C++ code</param>
        private void OutputMethodNode(CppMethodCodeNode methodCodeNode)
        {
            Out.WriteLine();
            Out.Write(methodCodeNode.CppCode);

            var alternateName = _compilation.NodeFactory.GetSymbolAlternateName(methodCodeNode);
            if (alternateName != null)
            {
                CppGenerationBuffer sb = new CppGenerationBuffer();
                sb.AppendLine();
                AppendCppMethodDeclaration(sb, methodCodeNode.Method, true, alternateName);
                sb.AppendLine();
                sb.Append("{");
                sb.Indent();
                sb.AppendLine();
                if (!methodCodeNode.Method.Signature.ReturnType.IsVoid)
                {
                    sb.Append("return ");
                }
                sb.Append(GetCppMethodDeclarationName(methodCodeNode.Method.OwningType, GetCppMethodName(methodCodeNode.Method)));
                sb.Append("(");
                AppendCppMethodCallParamList(sb, methodCodeNode.Method);
                sb.Append(");");
                sb.Exdent();
                sb.AppendLine();
                sb.Append("}");

                Out.Write(sb.ToString());
            }
        }
        private void OutputTypeNode(IEETypeNode typeNode, NodeFactory factory, CppGenerationBuffer forwardDefinitions, CppGenerationBuffer typeDefinitions, CppGenerationBuffer methodTable)
        {
            if (_emittedTypes == null)
            {
                _emittedTypes = new HashSet<TypeDesc>();
            }
            TypeDesc nodeType = typeNode.Type;
            if (nodeType.IsPointer || nodeType.IsByRef || _emittedTypes.Contains(nodeType))
                return;

            _emittedTypes.Add(nodeType);


            // Create Namespaces
            string mangledName = GetCppTypeName(nodeType);

            int nesting = 0;
            int current = 0;

            forwardDefinitions.AppendLine();
            for (;;)
            {
                int sep = mangledName.IndexOf("::", current);

                if (sep < 0)
                    break;

                if (sep != 0)
                {
                    // Case of a name not starting with ::
                    forwardDefinitions.Append("namespace " + mangledName.Substring(current, sep - current) + " { ");
                    typeDefinitions.Append("namespace " + mangledName.Substring(current, sep - current) + " { ");
                    typeDefinitions.Indent();
                    nesting++;
                }
                current = sep + 2;
            }

            forwardDefinitions.Append("class " + mangledName.Substring(current) + ";");

            // type definition
            typeDefinitions.Append("class " + mangledName.Substring(current));
            if (!nodeType.IsValueType)
            {
                // Don't emit inheritance if base type has not been marked for emission
                if (nodeType.BaseType != null && _emittedTypes.Contains(nodeType.BaseType))
                {
                    typeDefinitions.Append(" : public " + GetCppTypeName(nodeType.BaseType));
                }
            }
            typeDefinitions.Append(" {");
            typeDefinitions.AppendLine();
            typeDefinitions.Append("public:");
            typeDefinitions.Indent();

            // TODO: Enable once the dependencies are tracked for arrays
            // if (((DependencyNode)_compilation.NodeFactory.ConstructedTypeSymbol(t)).Marked)
            if (!nodeType.IsPointer && !nodeType.IsByRef)
            {
                typeDefinitions.AppendLine();
                typeDefinitions.Append("static MethodTable * __getMethodTable();");
            }
            if (typeNode is ConstructedEETypeNode)
            {
                OutputTypeFields(typeDefinitions, nodeType);

                IReadOnlyList<MethodDesc> virtualSlots = _compilation.NodeFactory.VTable(nodeType).Slots;

                int baseSlots = 0;
                var baseType = nodeType.BaseType;
                while (baseType != null)
                {
                    IReadOnlyList<MethodDesc> baseVirtualSlots = _compilation.NodeFactory.VTable(baseType).Slots;
                    if (baseVirtualSlots != null)
                        baseSlots += baseVirtualSlots.Count;
                    baseType = baseType.BaseType;
                }

                for (int slot = 0; slot < virtualSlots.Count; slot++)
                {
                    MethodDesc virtualMethod = virtualSlots[slot];
                    typeDefinitions.AppendLine();
                    typeDefinitions.Append(GetCodeForVirtualMethod(virtualMethod, baseSlots + slot));
                }

                if (nodeType.IsDelegate)
                {
                    typeDefinitions.AppendLine();
                    typeDefinitions.Append(GetCodeForDelegate(nodeType));
                }


                if (nodeType.HasStaticConstructor)
                {
                    _statics.AppendLine();
                    _statics.Append("bool __cctor_" + GetCppTypeName(nodeType).Replace("::", "__") + ";");
                }

                List<MethodDesc> methodList;
                if (_methodLists.TryGetValue(nodeType, out methodList))
                {
                    foreach (var m in methodList)
                    {
                        typeDefinitions.AppendLine();
                        AppendCppMethodDeclaration(typeDefinitions, m, false);
                    }
                }
            }
            typeDefinitions.AppendEmptyLine();
            typeDefinitions.Append("};");
            typeDefinitions.AppendEmptyLine();
            typeDefinitions.Exdent();

            while (nesting > 0)
            {
                forwardDefinitions.Append("};");
                typeDefinitions.Append("};");
                typeDefinitions.Exdent();
                nesting--;
            }
            typeDefinitions.AppendEmptyLine();

            // declare method table
            if (!nodeType.IsPointer && !nodeType.IsByRef)
            {
                methodTable.Append(GetCodeForObjectNode(typeNode as ObjectNode, factory));
                methodTable.AppendEmptyLine();
            }
        }

        private String GetCodeForReadyToRunHeader(ReadyToRunHeaderNode headerNode, NodeFactory factory)
        {
            CppGenerationBuffer rtrHeader = new CppGenerationBuffer();
            rtrHeader.Append(GetCodeForObjectNode(headerNode, factory));
            rtrHeader.AppendLine();
            rtrHeader.Append("void* RtRHeaderWrapper() {");
            rtrHeader.Indent();
            rtrHeader.AppendLine();
            rtrHeader.Append("static struct {");
            rtrHeader.AppendLine();
            rtrHeader.Append("unsigned char leftPadding[8];");
            rtrHeader.AppendLine();
            rtrHeader.Append("void* rtrHeader;");
            rtrHeader.AppendLine();
            rtrHeader.Append("unsigned char rightPadding[8];");
            rtrHeader.AppendLine();
            rtrHeader.Append("} rtrHeaderWrapper = {");
            rtrHeader.Indent();
            rtrHeader.AppendLine();
            rtrHeader.Append("{ 0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00 },");
            rtrHeader.AppendLine();
            rtrHeader.Append("(void*)");
            rtrHeader.Append(((ISymbolNode)headerNode).MangledName);
            rtrHeader.Append("(),");
            rtrHeader.AppendLine();
            rtrHeader.Append("{ 0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00 }");
            rtrHeader.AppendLine();
            rtrHeader.Append("};");
            rtrHeader.Exdent();
            rtrHeader.AppendLine();
            rtrHeader.Append("return (void *)&rtrHeaderWrapper;");
            rtrHeader.Exdent();
            rtrHeader.AppendLine();
            rtrHeader.Append("}");
            rtrHeader.AppendLine();

            return rtrHeader.ToString();
        }

        private void OutputExternCSignatures()
        {
            var sb = new CppGenerationBuffer();
            foreach (var externC in _externCSignatureMap)
            {
                string importName = externC.Key;
                // TODO: hacky special-case
                if (importName != "memmove" && importName != "malloc") // some methods are already declared by the CRT headers
                {
                    sb.AppendLine();
                    AppendCppMethodDeclaration(sb, null, false, importName, externC.Value);
                }
            }

            Out.Write(sb.ToString());
        }

        private void OutputMainMethodStub(MethodDesc entrypoint)
        {
            var sb = new CppGenerationBuffer();

            sb.AppendLine();
            if (_compilation.TypeSystemContext.Target.OperatingSystem == TargetOS.Windows)
            {
                sb.Append("int wmain(int argc, wchar_t * argv[]) { ");
            }
            else
            {
                sb.Append("int main(int argc, char * argv[]) {");
            }
            sb.Indent();

            sb.AppendLine();
            sb.Append("if (__initialize_runtime() != 0)");
            sb.Indent();
            sb.AppendLine();
            sb.Append("return -1;");
            sb.Exdent();
            sb.AppendEmptyLine();
            sb.AppendLine();
            sb.Append("ReversePInvokeFrame frame;");
            sb.AppendLine();
            sb.Append("__reverse_pinvoke(&frame);");

            sb.AppendEmptyLine();
            sb.AppendLine();
            sb.Append("::System_Private_CoreLib::Internal::Runtime::CompilerHelpers::StartupCodeHelpers::InitializeModules((intptr_t)RtRHeaderWrapper(), 2);");
            sb.AppendLine();
            sb.Append("int ret = ");
            sb.Append(GetCppMethodDeclarationName(entrypoint.OwningType, GetCppMethodName(entrypoint)));
            sb.Append("(argc, (intptr_t)argv);");

            sb.AppendEmptyLine();
            sb.AppendLine();
            sb.Append("__reverse_pinvoke_return(&frame);");
            sb.AppendLine();
            sb.Append("__shutdown_runtime();");

            sb.AppendLine();
            sb.Append("return ret;");
            sb.Exdent();
            sb.AppendLine();
            sb.Append("}");

            Out.Write(sb.ToString());
        }

        public void OutputCode(IEnumerable<DependencyNode> nodes, NodeFactory factory)
        {
            BuildMethodLists(nodes);

            Out.WriteLine("#include \"common.h\"");
            Out.WriteLine("#include \"CppCodeGen.h\"");
            Out.WriteLine();

            _statics = new CppGenerationBuffer();
            _statics.Indent();
            _gcStatics = new CppGenerationBuffer();
            _gcStatics.Indent();
            _threadStatics = new CppGenerationBuffer();
            _threadStatics.Indent();
            _gcThreadStatics = new CppGenerationBuffer();
            _gcThreadStatics.Indent();

            OutputNodes(nodes, factory);

            Out.Write("struct {");
            Out.Write(_statics.ToString());
            Out.Write("} __statics;");

            Out.Write("struct {");
            Out.Write(_gcStatics.ToString());
            Out.Write("} __gcStatics;");

            Out.Write("struct {");
            Out.Write(_gcStatics.ToString());
            Out.Write("} __gcThreadStatics;");

            OutputExternCSignatures();

            foreach (var node in nodes)
            {
                if (node is CppMethodCodeNode)
                    OutputMethodNode(node as CppMethodCodeNode);
            }

            // Try to locate the entrypoint method
            MethodDesc entrypoint = null;
            foreach (var alias in factory.NodeAliases)
                if (alias.Value == MainMethodRootProvider.ManagedEntryPointMethodName)
                    entrypoint = ((IMethodNode)alias.Key).Method;

            if (entrypoint != null)
            {
                OutputMainMethodStub(entrypoint);
            }

            Out.Dispose();
        }
    }
}