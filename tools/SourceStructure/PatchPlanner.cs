using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static partial class Program
{
    private static int Split(string path)
    {
        path = path.Replace('\\', '/');
        var text = Current(path);
        var root = Parse(text);
        var edits = new List<(int Start, int Length, string Replacement)>();
        var outputs = new List<(string Path, string Content)>();
        var ns = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().First();
        var prefix = text[..ns.SpanStart] + "namespace " + ns.Name + ";\n\n";
        var touched = new HashSet<TypeDeclarationSyntax>();
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(type => Lines(type.ToFullString()) > 700))
        {
            // A partial retains its own state, constructors, attributes and type identity.
            var groups = new List<(string Region, List<MemberDeclarationSyntax> Members)>();
            var current = new List<MemberDeclarationSyntax>();
            var size = 0;
            var region = "Members";
            var activeRegion = region;
            foreach (var member in type.Members)
            {
                var declaredRegion = member.GetLeadingTrivia().Where(trivia => trivia.HasStructure).Select(trivia => trivia.GetStructure())
                    .OfType<RegionDirectiveTriviaSyntax>().LastOrDefault();
                if (declaredRegion is not null) region = RegionName(declaredRegion.ToString());
                if (member is TypeDeclarationSyntax nested && Lines(nested.ToFullString()) > 700) continue;
                if (member is FieldDeclarationSyntax or EventFieldDeclarationSyntax or ConstructorDeclarationSyntax
                    || member is PropertyDeclarationSyntax { Initializer: not null }) continue;
                if (member.DescendantTrivia(descendIntoTrivia: true).Where(trivia => trivia.HasStructure).Select(trivia => trivia.GetStructure())
                    .OfType<DirectiveTriviaSyntax>().Any(directive => directive is not RegionDirectiveTriviaSyntax and not EndRegionDirectiveTriviaSyntax)) continue;
                var length = Lines(member.ToFullString());
                if (length > 900) throw new InvalidDataException("Single member needs a manual extraction: " + TypeName(type) + "." + MemberName(member));
                if ((size + length > 700 || region != activeRegion) && current.Count > 0)
                {
                    groups.Add((activeRegion, current)); current = new(); size = 0;
                }
                activeRegion = region;
                current.Add(member); size += length;
            }
            if (current.Count > 0) groups.Add((activeRegion, current));
            if (groups.Count == 0) continue;
            var chain = type.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Append(type).ToArray();
            foreach (var owner in chain) touched.Add(owner);
            foreach (var group in groups)
            {
                var responsibility = group.Region + "." + Responsibility(path, type.Identifier.Text, Selector(group.Members[0]).Split('/')[0]);
                var content = prefix;
                foreach (var owner in chain) content += TypeHeader(owner) + "\n{\n";
                foreach (var member in group.Members)
                {
                    content += WithoutRegions(member.ToFullString()) + "\n";
                    edits.Add((member.FullSpan.Start, member.FullSpan.Length, ""));
                }
                foreach (var owner in chain) content += "}\n";
                var fileName = string.Join(".", chain.Select(owner => owner.Identifier.Text)) + "." + responsibility + ".cs";
                var destination = Path.GetDirectoryName(path)!.Replace('\\', '/') + "/" + fileName;
                if (outputs.Any(output => output.Path == destination) || File.Exists(destination))
                    throw new InvalidDataException("Responsibility name is not unique: " + destination);
                if (Lines(content) > 1000) throw new InvalidDataException("Output too large: " + destination);
                outputs.Add((destination, content));
            }
        }
        foreach (var type in touched.Where(type => !type.Modifiers.Any(SyntaxKind.PartialKeyword)))
            edits.Add((type.Keyword.SpanStart, 0, "partial "));
        foreach (var edit in edits.OrderByDescending(edit => edit.Start)) text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
        text = WithoutRegions(text);
        if (Lines(text) > 1000) throw new InvalidDataException("State shell still exceeds 1000 lines: " + path + " (" + Lines(text) + ")");
        var patches = new List<string> { "*** Begin Patch\n*** Delete File: " + path + "\n*** End Patch" };
        AddFilePatches(patches, path, text);
        foreach (var output in outputs) AddFilePatches(patches, output.Path, output.Content);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(patches));
        return 0;
    }

    private static string Responsibility(string path, string type, string first)
    {
        // These names identify original entry points rather than arbitrary numbered chunks.
        var name = new string(first.TakeWhile(character => char.IsLetterOrDigit(character) || character == '_').ToArray()).TrimStart('_');
        if (name.Length == 0) name = "Declarations";
        return name;
    }

    private static string RegionName(string text)
    {
        var name = text.Replace("#region", "", StringComparison.Ordinal).Trim();
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["数学"] = "Numeric", ["文件"] = "FileSystem", ["文本"] = "Text", ["搜索"] = "Search",
            ["系统"] = "SystemInterop", ["UI"] = "UserInterface", ["Debug"] = "Diagnostics", ["WPF"] = "Wpf",
            ["声明"] = "Declarations", ["自定义类"] = "ValueTypes", ["预检测"] = "Precheck", ["开始"] = "LaunchCoordinator",
            ["档案验证"] = "Authentication", ["主模块"] = "AuthenticationFlow", ["第三方验证类型"] = "ThirdPartyIdentity",
            ["正版验证类型"] = "MicrosoftIdentity", ["离线验证类型"] = "OfflineIdentity", ["正版验证"] = "MicrosoftAuthentication",
            ["第三方验证"] = "ThirdPartyAuthentication", ["离线验证"] = "OfflineAuthentication", ["Java 处理"] = "JavaSelection",
            ["启动参数"] = "LaunchArguments", ["解压 Natives"] = "NativeExtraction", ["启动与前后处理"] = "GameProcessLifecycle",
            ["初始化"] = "Initialization", ["基础"] = "Identity", ["信息项"] = "Metadata", ["加载"] = "Loading",
            ["UI 化"] = "Presentation", ["管理"] = "Management", ["选择"] = "Selection", ["筛选"] = "Filtering",
            ["排序"] = "Sorting", ["下边栏"] = "SelectionActions",
            ["单个资源项"] = "ResourceActions", ["文件夹导航"] = "FolderNavigation", ["模组信息缓存"] = "FileInfoCache",
            ["原理图文件详细信息显示"] = "SchematicDetails", ["Litematic 文件处理"] = "LitematicReader",
            ["Schem 文件处理"] = "SchemReader", ["Schematic 文件处理"] = "SchematicReader", ["NBT 结构文件处理"] = "StructureReader",
            ["加载步骤标记"] = "LoadState", ["快速下载（资源卡片下载按钮）"] = "ResourceDownloads",
        };
        if (names.TryGetValue(name, out var translated)) return translated;
        var ascii = new string(name.TakeWhile(character => character < 128 && (char.IsLetterOrDigit(character) || character == '_')).ToArray());
        return ascii.Length > 0 ? ascii : "ViewActions";
    }

    private sealed record SplitSpec(string Type, string Responsibility, string First, string Last);

    private static int SeparateTypes(string path)
    {
        path = path.Replace('\\', '/');
        var text = Current(path);
        var root = Parse(text);
        var ns = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().First();
        var declarations = ns.Members.ToArray();
        var prefix = text[..ns.SpanStart] + "namespace " + ns.Name + ";\n\n";
        var patches = new List<string> { "*** Begin Patch\n*** Delete File: " + path + "\n*** End Patch" };
        AddFilePatches(patches, path, WithoutRegions(prefix + declarations[0].ToFullString()));
        foreach (var declaration in declarations.Skip(1))
        {
            var name = declaration is BaseTypeDeclarationSyntax type ? type.Identifier.Text : MemberName(declaration);
            var destination = Path.GetDirectoryName(path)!.Replace('\\', '/') + "/" + name + ".cs";
            if (File.Exists(destination)) throw new IOException("Type output already exists: " + destination);
            AddFilePatches(patches, destination, WithoutRegions(prefix + declaration.ToFullString()));
        }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(patches));
        return 0;
    }

    // This mode emits reviewable patches only. It never writes or replaces repository sources.
    private static int Plan(string[] args)
    {
        if (args.Length < 5 || (args.Length - 1) % 4 != 0)
            throw new ArgumentException("plan <path> (<type> <responsibility> <first-member> <last-member>)+");
        var path = args[0].Replace('\\', '/');
        var source = Current(path);
        var syntax = Parse(source);
        var patches = new List<(string Path, string Content)>();
        var originalMembers = new Dictionary<TypeDeclarationSyntax, HashSet<MemberDeclarationSyntax>>();
        var affectedTypes = new HashSet<TypeDeclarationSyntax>();
        var headerEnd = syntax.Members.First().SpanStart;
        var usings = source[..headerEnd];
        var ns = syntax.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().First();
        var namespaceHeader = "namespace " + ns.Name + ";\n\n";
        if (ns is FileScopedNamespaceDeclarationSyntax) usings = source[..ns.SpanStart];
        for (var index = 1; index < args.Length; index += 4)
        {
            var spec = new SplitSpec(args[index], args[index + 1], args[index + 2], args[index + 3]);
            var type = syntax.DescendantNodes().OfType<TypeDeclarationSyntax>().Single(item => item.Identifier.Text == spec.Type);
            var list = type.Members.ToList();
            var first = list.FindIndex(member => Selector(member).Split('/')[0] == spec.First.Split('/')[0]);
            var last = list.FindLastIndex(member => Selector(member).Split('/')[0] == spec.Last.Split('/')[0]);
            if (first < 0 || last < first) throw new InvalidDataException($"Range not found: {spec.Type} {spec.First}..{spec.Last}");
            var selected = list.Skip(first).Take(last - first + 1)
                .Where(member => member is not FieldDeclarationSyntax and not EventFieldDeclarationSyntax and not ConstructorDeclarationSyntax
                    && member is not PropertyDeclarationSyntax { Initializer: not null }).ToArray();
            // All executable initialization remains in the original file in exactly its original order.
            if (selected.Any(member => member is FieldDeclarationSyntax || member is EventFieldDeclarationSyntax
                || member is PropertyDeclarationSyntax { Initializer: not null } || member is ConstructorDeclarationSyntax))
                throw new InvalidDataException("A range contains state initialization; choose a narrower range.");
            if (selected.Any(member => member.DescendantTrivia(descendIntoTrivia: true).Where(trivia => trivia.HasStructure)
                .Select(trivia => trivia.GetStructure()).OfType<DirectiveTriviaSyntax>()
                .Any(directive => directive is not RegionDirectiveTriviaSyntax and not EndRegionDirectiveTriviaSyntax)))
                throw new InvalidDataException("Cannot move conditional compilation without its surrounding directives.");
            if (!originalMembers.TryGetValue(type, out var removed)) originalMembers[type] = removed = new();
            foreach (var member in selected)
                if (!removed.Add(member)) throw new InvalidDataException("Overlapping split ranges.");
            var chain = type.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Append(type).ToArray();
            foreach (var parent in chain) affectedTypes.Add(parent);
            var content = usings + namespaceHeader;
            foreach (var parent in chain)
                content += TypeHeader(parent) + "\n{\n";
            foreach (var member in selected) content += WithoutRegions(member.ToFullString()) + "\n";
            for (var j = chain.Length - 1; j >= 0; j--) content += "}\n";
            var output = Path.GetDirectoryName(path)!.Replace('\\', '/') + "/" + spec.Type + "." + spec.Responsibility + ".cs";
            if (File.Exists(output)) throw new IOException("Output already exists: " + output);
            if (Lines(content) > 1000) throw new InvalidDataException($"Output is too large: {output} ({Lines(content)} lines)");
            patches.Add((output, content));
        }
        var edits = new List<(int Start, int Length, string Replacement)>();
        foreach (var pair in originalMembers)
            foreach (var member in pair.Value) edits.Add((member.FullSpan.Start, member.FullSpan.Length, ""));
        foreach (var type in affectedTypes.Where(type => !type.Modifiers.Any(SyntaxKind.PartialKeyword)))
            edits.Add((type.Keyword.SpanStart, 0, "partial "));
        foreach (var edit in edits.OrderByDescending(edit => edit.Start)) source = source.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
        source = WithoutRegions(source);
        var nativePatches = new List<string>();
        // Windows argv has a size limit. Emit bounded native patches, never shell file rewrites.
        nativePatches.Add("*** Begin Patch\n*** Delete File: " + path + "\n*** End Patch");
        AddFilePatches(nativePatches, path, source);
        foreach (var patch in patches)
        {
            AddFilePatches(nativePatches, patch.Path, patch.Content);
        }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(nativePatches));
        return 0;
    }

    private static void AddFilePatches(List<string> patches, string path, string content)
    {
        var lines = content.TrimEnd('\n').Split('\n');
        var offset = 0;
        while (offset < lines.Length)
        {
            var chunk = new List<string>();
            var characters = 0;
            while (offset + chunk.Count < lines.Length && characters + lines[offset + chunk.Count].Length < 16000)
            {
                var line = lines[offset + chunk.Count];
                chunk.Add(line);
                characters += line.Length + 2;
            }
            if (chunk.Count == 0) throw new InvalidDataException("Source line exceeds native patch budget.");
            var patch = "*** Begin Patch\n";
            if (offset == 0) patch += "*** Add File: " + path + "\n";
            else
            {
                patch += "*** Update File: " + path + "\n@@\n";
                foreach (var line in lines.Skip(Math.Max(0, offset - 8)).Take(Math.Min(8, offset))) patch += " " + line + "\n";
            }
            patch += string.Join("\n", chunk.Select(line => "+" + line)) + "\n";
            if (offset > 0) patch += "*** End of File\n";
            patch += "*** End Patch";
            patches.Add(patch);
            offset += chunk.Count;
        }
    }

    private static string Selector(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.Text + "/" + method.ParameterList.Parameters.Count,
        _ => MemberName(member),
    };

    private static string TypeHeader(TypeDeclarationSyntax type)
    {
        var modifiers = string.Join(" ", type.Modifiers.Where(token => !token.IsKind(SyntaxKind.PartialKeyword)).Select(token => token.Text));
        return modifiers + " partial " + type.Keyword.Text + " " + type.Identifier.Text + type.TypeParameterList;
    }

    private static string WithoutRegions(string text) => string.Join("\n", Normalize(text).Split('\n')
        .Where(line => !line.TrimStart().StartsWith("#region", StringComparison.Ordinal) && !line.TrimStart().StartsWith("#endregion", StringComparison.Ordinal)));
}
