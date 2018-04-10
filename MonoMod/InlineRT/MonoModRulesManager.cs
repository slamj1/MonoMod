﻿using Mono.Cecil;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;

namespace MonoMod.InlineRT {
    public static class MonoModRulesManager {

        public static IDictionary<long, WeakReference> ModderMap = new Dictionary<long, WeakReference>();
        public static ObjectIDGenerator ModderIdGen = new ObjectIDGenerator();

        private static Assembly MonoModAsm = Assembly.GetExecutingAssembly();

        public static MonoModder Modder {
            get {
                StackTrace st = new StackTrace();
                for (int i = 1; i < st.FrameCount; i++) {
                    StackFrame frame = st.GetFrame(i);
                    MethodBase method = frame.GetMethod();
                    Assembly asm = method.DeclaringType.Assembly;
                    if (asm != MonoModAsm)
                        return GetModder(method.DeclaringType.Assembly.GetName().Name);
                }
                return null;
            }
        }

        public static Type RuleType {
            get {
                StackTrace st = new StackTrace();
                for (int i = 1; i < st.FrameCount; i++) {
                    StackFrame frame = st.GetFrame(i);
                    MethodBase method = frame.GetMethod();
                    Assembly asm = method.DeclaringType.Assembly;
                    if (asm != MonoModAsm)
                        return method.DeclaringType;
                }
                return null;
            }
        }

        public static void Register(MonoModder self) {
            bool firstTime;
            ModderMap[ModderIdGen.GetId(self, out firstTime)] = new WeakReference(self);
            if (!firstTime)
                throw new InvalidOperationException("MonoModder instance already registered in MMILProxyManager");
        }

        public static long GetId(MonoModder self) {
            bool firstTime;
            long id = ModderIdGen.GetId(self, out firstTime);
            if (firstTime)
                throw new InvalidOperationException("MonoModder instance wasn't registered in MMILProxyManager");
            return id;
        }

        public static MonoModder GetModder(string asmName) {
            string idString = asmName;
            idString = idString.Substring(idString.IndexOf("-ID:") + 4) + ' ';
            idString = idString.Substring(0, idString.IndexOf(' '));
            long id;
            if (!long.TryParse(idString, out id))
                throw new InvalidOperationException($"Cannot get MonoModder ID from assembly name {asmName}");
            return (MonoModder) ModderMap[id].Target;
        }

        public static Type ExecuteRules(this MonoModder self, TypeDefinition orig) {
            ModuleDefinition wrapper = ModuleDefinition.CreateModule(
                $"{orig.Module.Name.Substring(0, orig.Module.Name.Length - 4)}.MonoModRules -ID:{GetId(self)} -MMILRT",
                new ModuleParameters() {
                    Architecture = orig.Module.Architecture,
                    AssemblyResolver = self.AssemblyResolver,
                    Kind = ModuleKind.Dll,
                    MetadataResolver = orig.Module.MetadataResolver,
                    Runtime = TargetRuntime.Net_2_0
                }
            );
            MonoModder wrapperMod = new MonoModder() {
                Module = wrapper,

                Logger = (modder, msg) => self.Log("[MonoModRule] " + msg),

                CleanupEnabled = false,

                DependencyDirs = self.DependencyDirs,
                MissingDependencyResolver = self.MissingDependencyResolver
            };
            wrapperMod.WriterParameters.WriteSymbols = false;
            wrapperMod.WriterParameters.SymbolWriterProvider = null;

            // Only add a copy of the map - adding the MMILRT asm itself to the map only causes issues.
            wrapperMod.DependencyCache.AddRange(self.DependencyCache);
            foreach (KeyValuePair<ModuleDefinition, List<ModuleDefinition>> mapping in self.DependencyMap)
                wrapperMod.DependencyMap[mapping.Key] = new List<ModuleDefinition>(mapping.Value);

            // Required as the relinker only deep-relinks if the method the type comes from is a mod.
            // Fixes nasty reference import sharing issues.
            wrapperMod.Mods.Add(self.Module);

            wrapperMod.Relinker = (mtp, context) =>
                mtp is TypeReference && ((TypeReference) mtp).FullName == orig.FullName ?
                    wrapper.GetType(orig.FullName) :
                wrapperMod.DefaultRelinker(mtp, context);

            wrapperMod.PrePatchType(orig, forceAdd: true);
            wrapperMod.PatchType(orig);
            TypeDefinition rulesCecil = wrapper.GetType(orig.FullName);
            wrapperMod.PatchRefsInType(rulesCecil);

            Assembly asm;
            using (MemoryStream asmStream = new MemoryStream()) {
                wrapperMod.Write(asmStream);
                asm = Assembly.Load(asmStream.GetBuffer());
            }

            /**//*
            using (FileStream debugStream = File.OpenWrite(Path.Combine(
                self.DependencyDirs[0], $"{orig.Module.Name.Substring(0, orig.Module.Name.Length - 4)}.MonoModRules-MMILRT.dll")))
                wrapperMod.Write(debugStream);
            /**/

            Type rules = asm.GetType(orig.FullName);
            RuntimeHelpers.RunClassConstructor(rules.TypeHandle);

            return rules;
        }

    }
}