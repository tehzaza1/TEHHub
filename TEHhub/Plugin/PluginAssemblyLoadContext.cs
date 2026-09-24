namespace TEHhub.Plugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using System.Runtime.Loader;
    using TEHhub.Offsets;

    internal class PluginAssemblyLoadContext : AssemblyLoadContext
    {
        private static readonly IReadOnlyDictionary<string, Assembly> SharedAssemblies =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase)
            {
                [typeof(IPCore).Assembly.GetName().Name!] = typeof(IPCore).Assembly,
                [typeof(GameProcessDetails).Assembly.GetName().Name!] = typeof(GameProcessDetails).Assembly,
            };

        private readonly AssemblyDependencyResolver resolver;
        private readonly string dependenciesDirectory;

        public PluginAssemblyLoadContext(string assemblyLocation)
            : base(isCollectible: true)
        {
            this.resolver = new AssemblyDependencyResolver(assemblyLocation);
            this.dependenciesDirectory = Path.Combine(Path.GetDirectoryName(assemblyLocation)!, "Dependencies");
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name != null &&
                SharedAssemblies.TryGetValue(assemblyName.Name, out var sharedAssembly))
            {
                return sharedAssembly;
            }

            var path = this.resolver.ResolveAssemblyToPath(assemblyName);
            if (path != null && File.Exists(path))
            {
                return this.LoadFromAssemblyPath(path);
            }

            if (assemblyName.Name != null)
            {
                var dependencyPath = Path.Combine(this.dependenciesDirectory, assemblyName.Name + ".dll");
                if (File.Exists(dependencyPath))
                {
                    return this.LoadFromAssemblyPath(dependencyPath);
                }
            }

            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = this.resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path == null || !File.Exists(path) ? IntPtr.Zero : this.LoadUnmanagedDllFromPath(path);
        }
    }
}
