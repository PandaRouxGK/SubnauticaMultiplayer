using Mono.Cecil;
using Mono.Cecil.Cil;

// Usage:
//   ApiDump <assembly.dll> <filtre>            -> liste les types dont le nom contient <filtre>
//   ApiDump callers <assembly.dll> <methode>   -> liste les methodes qui APPELENT <methode> (scan IL)

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: ApiDump <assembly.dll> <filtre>");
    Console.Error.WriteLine("       ApiDump callers <assembly.dll> <methode>");
    return 1;
}

var callersMode = string.Equals(args[0], "callers", StringComparison.OrdinalIgnoreCase);
var assemblyPath = callersMode ? (args.Length > 1 ? args[1] : null) : args[0];
var filter = callersMode ? (args.Length > 2 ? args[2] : null) : args[1];

if (string.IsNullOrEmpty(assemblyPath) || string.IsNullOrEmpty(filter))
{
    Console.Error.WriteLine("arguments manquants");
    return 1;
}

if (!File.Exists(assemblyPath))
{
    Console.Error.WriteLine($"introuvable: {assemblyPath}");
    return 1;
}

var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
var module = assembly.MainModule;

return callersMode ? ListCallers(module, filter) : DumpTypes(module, filter);

static int ListCallers(ModuleDefinition module, string filter)
{
    var found = 0;

    foreach (var type in EnumerateAllTypes(module))
    {
        foreach (var method in type.Methods)
        {
            if (!method.HasBody)
            {
                continue;
            }

            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.OperandType != OperandType.InlineMethod)
                {
                    continue;
                }

                if (instruction.Operand is not MethodReference target)
                {
                    continue;
                }

                if (target.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                found++;
                Console.WriteLine(
                    $"  {type.FullName}.{method.Name}  ->  {target.DeclaringType.FullName}.{target.Name}");
            }
        }
    }

    Console.WriteLine(found == 0
        ? $"\nAucun appel a « {filter} » trouve."
        : $"\n{found} appel(s) a « {filter} ».");
    return 0;
}

static int DumpTypes(ModuleDefinition module, string filter)
{
    var matched = 0;

    foreach (var type in EnumerateAllTypes(module))
    {
        if (type.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
        {
            continue;
        }

        matched++;
        var kind = type.IsInterface ? "interface" : type.IsEnum ? "enum" : type.IsValueType ? "struct" : "class";
        Console.WriteLine($"\n=== {kind} {type.FullName}  (base: {type.BaseType?.FullName ?? "-"}) ===");

        if (type.HasFields)
        {
            // Les champs prives sont inclus : le modele du joueur, par exemple, n'est expose
            // ni en public ni en propriete, et c'est justement ce qu'on doit retrouver.
            foreach (var f in type.Fields)
            {
                var visibility = f.IsPublic ? "public " : f.IsAssembly ? "internal " : f.IsPrivate ? "prive " : "protege ";
                var mods = (f.IsStatic ? "static " : "") + (f.IsInitOnly ? "readonly " : "");
                Console.WriteLine($"  [champ]     {visibility}{mods}{f.FieldType.Name} {f.Name}");
            }
        }

        if (type.HasProperties)
        {
            foreach (var p in type.Properties)
            {
                var accessors = (p.GetMethod != null ? "get;" : "") + (p.SetMethod != null ? "set;" : "");
                Console.WriteLine($"  [prop]      {p.PropertyType.Name} {p.Name} {{{accessors}}}");
            }
        }

        if (type.HasMethods)
        {
            foreach (var m in type.Methods.Where(m => !m.IsGetter && !m.IsSetter))
            {
                // Les methodes privees sont incluses : ce sont souvent elles qu'on doit patcher.
                var visibility = m.IsPublic ? "public " : m.IsAssembly ? "internal " : "prive ";
                var mods = (m.IsStatic ? "static " : "") + (m.IsVirtual ? "virtual " : "");
                var parameters = string.Join(", ", m.Parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                Console.WriteLine($"  [methode]   {visibility}{mods}{m.ReturnType.Name} {m.Name}({parameters})");
            }
        }
    }

    Console.WriteLine(matched == 0
        ? $"\nAucun type ne correspond a « {filter} »."
        : $"\n{matched} type(s) correspondant a « {filter} ».");
    return 0;
}

static IEnumerable<TypeDefinition> EnumerateAllTypes(ModuleDefinition module)
{
    foreach (var type in module.Types)
    {
        foreach (var t in Walk(type))
        {
            yield return t;
        }
    }
}

static IEnumerable<TypeDefinition> Walk(TypeDefinition type)
{
    yield return type;
    foreach (var nested in type.NestedTypes)
    {
        foreach (var t in Walk(nested))
        {
            yield return t;
        }
    }
}
