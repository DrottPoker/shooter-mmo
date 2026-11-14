using System;
using Mmorpg.Client.Core;
using UnityEditor;
using UnityEditor.Build;

namespace Mmorpg.Client.Editor
{
    // The player settings of each kind of player build. The project settings hold the release
    // kind's values, so a build from the Build Profiles window and a test player of the Unity
    // Test Framework compile the C++ the way a release build does; a development build applies
    // its own values for the build and restores the committed ones afterwards.
    //
    // - Stripping: Low for both kinds, so the development builds that the tests and the playtests
    //   run strip exactly what a release build strips. Low removes unreachable code from the
    //   engine, the class libraries, and the project's own assemblies by conservative rules that
    //   leave reflection alone; Medium and High remove more and need link.xml entries for code
    //   that only reflection reaches, which no test proves complete.
    // - Code generation: a development build generates shared generic code (OptimizeSize), which
    //   builds faster; a release build generates code per generic instance (OptimizeSpeed),
    //   which runs faster.
    // - C++ compiler configuration: Release for a development build, optimized per file and
    //   quicker to compile; Master for a release build, which adds whole-program optimization at
    //   link time and takes the longest. Debug would compile faster still but runs the C++
    //   unoptimized, so a development player would no longer show the frame times of a release.
    public sealed class ClientBuildKindSettings
    {
        public static readonly ClientBuildKindSettings Development = new ClientBuildKindSettings(
            ClientBuildKind.Development,
            ManagedStrippingLevel.Low,
            Il2CppCodeGeneration.OptimizeSize,
            Il2CppCompilerConfiguration.Release,
            BuildOptions.Development);

        public static readonly ClientBuildKindSettings Release = new ClientBuildKindSettings(
            ClientBuildKind.Release,
            ManagedStrippingLevel.Low,
            Il2CppCodeGeneration.OptimizeSpeed,
            Il2CppCompilerConfiguration.Master,
            BuildOptions.None);

        private ClientBuildKindSettings(
            ClientBuildKind kind,
            ManagedStrippingLevel strippingLevel,
            Il2CppCodeGeneration codeGeneration,
            Il2CppCompilerConfiguration compilerConfiguration,
            BuildOptions options)
        {
            Kind = kind;
            StrippingLevel = strippingLevel;
            CodeGeneration = codeGeneration;
            CompilerConfiguration = compilerConfiguration;
            Options = options;
        }

        public ClientBuildKind Kind { get; }

        public ScriptingImplementation ScriptingBackend
        {
            get { return ScriptingImplementation.IL2CPP; }
        }

        public ManagedStrippingLevel StrippingLevel { get; }

        public Il2CppCodeGeneration CodeGeneration { get; }

        public Il2CppCompilerConfiguration CompilerConfiguration { get; }

        public BuildOptions Options { get; }

        public static ClientBuildKindSettings For(ClientBuildKind kind)
        {
            switch (kind)
            {
                case ClientBuildKind.Development:
                    return Development;
                case ClientBuildKind.Release:
                    return Release;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "The Editor is no build kind.");
            }
        }

        // Applies the kind's values to the Windows player settings until the scope is disposed,
        // which puts back the values the project had.
        public IDisposable Apply()
        {
            var target = NamedBuildTarget.Standalone;
            var previous = new Values(
                PlayerSettings.GetScriptingBackend(target),
                PlayerSettings.GetManagedStrippingLevel(target),
                PlayerSettings.GetIl2CppCodeGeneration(target),
                PlayerSettings.GetIl2CppCompilerConfiguration(target));
            Set(new Values(ScriptingBackend, StrippingLevel, CodeGeneration, CompilerConfiguration));
            return new Restore(previous);
        }

        private static void Set(Values values)
        {
            var target = NamedBuildTarget.Standalone;
            PlayerSettings.SetScriptingBackend(target, values.Backend);
            PlayerSettings.SetManagedStrippingLevel(target, values.Stripping);
            PlayerSettings.SetIl2CppCodeGeneration(target, values.CodeGeneration);
            PlayerSettings.SetIl2CppCompilerConfiguration(target, values.Compiler);
        }

        private readonly struct Values
        {
            public Values(
                ScriptingImplementation backend,
                ManagedStrippingLevel stripping,
                Il2CppCodeGeneration codeGeneration,
                Il2CppCompilerConfiguration compiler)
            {
                Backend = backend;
                Stripping = stripping;
                CodeGeneration = codeGeneration;
                Compiler = compiler;
            }

            public ScriptingImplementation Backend { get; }

            public ManagedStrippingLevel Stripping { get; }

            public Il2CppCodeGeneration CodeGeneration { get; }

            public Il2CppCompilerConfiguration Compiler { get; }
        }

        private sealed class Restore : IDisposable
        {
            private readonly Values previous;
            private bool disposed;

            public Restore(Values values)
            {
                previous = values;
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                Set(previous);
            }
        }
    }
}
