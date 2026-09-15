using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

class Inspector
{
    static void Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : "/tmp/tscmp/managed";
        var files = Directory.GetFiles(dir, "*.dll").ToList();
        var resolver = new PathAssemblyResolver(files);
        using var mlc = new MetadataLoadContext(resolver, coreAssemblyName: "mscorlib");

        var targets = args.Skip(1).ToArray();

        // search every assembly in the folder for the requested type names
        var loaded = new List<Assembly>();
        foreach (var f in files)
        {
            try { loaded.Add(mlc.LoadFromAssemblyPath(f)); } catch { }
        }

        foreach (var name in targets)
        {
            Type? t = null;
            Assembly? owner = null;
            foreach (var a in loaded)
            {
                try
                {
                    var c = a.GetTypes().FirstOrDefault(x => x.FullName == name || x.Name == name);
                    if (c != null) { t = c; owner = a; break; }
                }
                catch { }
            }
            Console.WriteLine($"=== {name} ===  [{owner?.GetName().Name ?? "not found"}]  {t?.FullName}");
            if (t == null) { Console.WriteLine("  NOT FOUND\n"); continue; }
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var f in t.GetFields(F))
            {
                string val = "";
                if (f.IsLiteral) { try { val = " = " + f.GetRawConstantValue(); } catch { } }
                Console.WriteLine($"  F {f.FieldType.Name} {f.Name}{val}");
            }
            foreach (var p in t.GetProperties(F))
                Console.WriteLine($"  P {p.PropertyType.Name} {p.Name}");
            foreach (var m in t.GetMethods(F).Concat<MethodBase>(t.GetConstructors(F)))
            {
                var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                var ret = m is MethodInfo mi ? mi.ReturnType.Name : "ctor";
                Console.WriteLine($"  M {ret} {m.Name}({ps})");
            }
            Console.WriteLine();
        }
    }
}
