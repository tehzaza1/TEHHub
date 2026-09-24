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
        private readonly string pluginDirectory;

        public PluginAssemblyLoadContext(string assemblyLocation)
            : base(isCollectible: true)
        {
            this.resolver = new AssemblyDependencyResolver(assemblyLocation);
            this.pluginDirectory = Path.GetDirectoryName(assemblyLocation)!;
            this.dependenciesDirectory = Path.Combine(this.pluginDirectory, "Dependencies");
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
            if (path != null && File.Exists(path))
            {
                return this.LoadUnmanagedDllFromPath(path);
            }

            // AutoExile2 stages SQLitePCLRaw's RID-specific native asset beside the plugin's
            // deps manifest. Resolve it explicitly when AssemblyDependencyResolver does not
            // return the staged path after the plugin is copied into its own directory.
            if (string.Equals(
                    Path.GetFileNameWithoutExtension(unmanagedDllName),
                    "e_sqlite3",
                    StringComparison.OrdinalIgnoreCase))
            {
                var sqlitePath = Path.Combine(this.pluginDirectory, "runtimes", "win-x64", "native", "e_sqlite3.dll");
                if (File.Exists(sqlitePath))
                {
                    return this.LoadUnmanagedDllFromPath(sqlitePath);
                }
            }

            return IntPtr.Zero;
        }
    }
}
