using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Seed;
using ALDevToolbox.Domain.ValueObjects;
using Tomlyn;

namespace ALDevToolbox.Services.Templates;

/// <summary>
/// Bridge between the persisted <see cref="RuntimeTemplate"/> shape and the
/// <c>template.toml</c> document format under the unified-extensions model.
/// The DB remains the source of truth — this class is editor-only
/// serialisation. The TOML schema lives in
/// <c>.design/unified-extensions.md</c> and <c>.design/templates-and-seeding.md</c>.
/// </summary>
public static class TemplateTomlMapper
{
    private static readonly TomlSerializerOptions TomlOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    private static readonly JsonSerializerOptions JsonOptions = PersistenceJson.Options;


    // ===== ToToml =====

    /// <summary>
    /// Serialises a template to its <c>template.toml</c> form. The two JSON
    /// columns are unpacked into <c>[defaults]</c> and <c>[appSourceCop]</c>
    /// tables. The recursive folder tree under each extension is emitted by
    /// hand (Tomlyn's reflection serialiser produces inline arrays that are
    /// unreadable for non-trivial templates).
    /// </summary>
    public static string ToToml(RuntimeTemplate template)
    {
        var seed = BuildHeaderSeed(template);

        // Serialise just the metadata + defaults sections via Tomlyn, then
        // strip its empty extensions array and append the [[extensions]]
        // blocks by hand.
        var head = TomlSerializer.Serialize(seed, TomlOptions);
        head = EmptyExtensionsLineRegex.Replace(head, string.Empty).TrimEnd();

        var sb = new StringBuilder(head);
        AppendWorkspaceSettings(sb, template.CodeWorkspaceJson);
        foreach (var ext in template.WorkspaceExtensions.OrderBy(e => e.Ordering))
        {
            AppendExtension(sb, ext);
        }
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Serialises an authoring payload to its <c>template.toml</c> form. Used
    /// by the admin form-editor's Form → TOML mode switch so the document the
    /// user sees in the TOML pane reflects the structured-form edits without
    /// a round-trip through the database.
    /// </summary>
    public static string ToToml(TemplateAuthoring authoring)
    {
        var defaults = string.IsNullOrWhiteSpace(authoring.DefaultsJson)
            ? new TemplateDefaults()
            : JsonSerializer.Deserialize<TemplateDefaults>(authoring.DefaultsJson, JsonOptions) ?? new TemplateDefaults();
        var appSourceCop = string.IsNullOrWhiteSpace(authoring.AppSourceCopJson)
            ? new AppSourceCopSettings()
            : JsonSerializer.Deserialize<AppSourceCopSettings>(authoring.AppSourceCopJson, JsonOptions) ?? new AppSourceCopSettings();

        var seed = BuildHeaderSeed(authoring, defaults, appSourceCop);
        var head = TomlSerializer.Serialize(seed, TomlOptions);
        head = EmptyExtensionsLineRegex.Replace(head, string.Empty).TrimEnd();

        var sb = new StringBuilder(head);
        AppendWorkspaceSettings(sb, authoring.CodeWorkspaceJson);
        foreach (var ext in authoring.Extensions)
        {
            AppendExtension(sb, ext);
        }
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Hand-emits the optional <c>[workspace_settings]</c> block (issue #61
    /// per-template overlay). Uses a TOML multi-line *literal* string
    /// (<c>'''…'''</c>) so the embedded JSON renders verbatim — basic
    /// triple-quoted strings (<c>"""…"""</c>) would interpret backslash
    /// escapes inside the JSON. Tomlyn's reflection serialiser is bypassed
    /// for this property; <see cref="TemplateSeed.WorkspaceSettings"/> is
    /// never populated when serialising so no auto-emitted block needs
    /// stripping.
    /// </summary>
    private static void AppendWorkspaceSettings(StringBuilder sb, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        // Defensive: TOML literal strings cannot contain three single quotes
        // in a row. JSON has no reason to, but if it does we fall back to
        // omitting the block rather than emitting un-parseable TOML.
        if (json.Contains("'''", StringComparison.Ordinal)) return;
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("[workspace_settings]");
        sb.Append("json = '''").AppendLine();
        sb.Append(json.TrimEnd('\r', '\n', ' ', '\t')).AppendLine();
        sb.Append("'''");
    }

    private static TemplateSeed BuildHeaderSeed(TemplateAuthoring authoring, TemplateDefaults defaults, AppSourceCopSettings appSourceCop) => new()
    {
        Template = new TemplateMetaSeed
        {
            Key = authoring.Key,
            Runtime = authoring.Runtime,
            Name = authoring.Name,
            Description = authoring.Description,
            CoreIdRangeFrom = authoring.CoreIdRangeFrom,
            CoreIdRangeTo = authoring.CoreIdRangeTo,
            ModuleIdRangeStart = authoring.ModuleIdRangeStart,
            ModuleIdRangeSize = authoring.ModuleIdRangeSize,
            IsDefault = authoring.IsDefault,
            // TOML carries the sentinel string in the same slot as the FK key
            // so the round-trip stays a single field. The mapper at parse
            // time recognises the sentinel and lifts it onto the authoring
            // bool.
            DefaultApplicationVersion = authoring.DefaultApplicationVersionLatest
                ? ApplicationVersionService.LatestSentinel
                : authoring.DefaultApplicationVersionKey,
            DefaultModules = authoring.DefaultModuleKeys
                .Select(k => new TemplateDefaultModuleSeed { Key = k })
                .ToList(),
            IncludedFiles = authoring.IncludedFilePaths?.ToList() ?? new List<string>(),
            RootFolders = authoring.RootFolderPaths?.ToList() ?? new List<string>(),
        },
        Defaults = BuildDefaultsSeed(defaults),
        AppSourceCop = new AppSourceCopSeed
        {
            Include = appSourceCop.Include,
            MandatoryPrefix = appSourceCop.MandatoryPrefix,
            SupportedCountries = appSourceCop.SupportedCountries.ToList(),
        },
    };

    private static TemplateSeed BuildHeaderSeed(RuntimeTemplate template)
    {
        var defaults = template.Defaults ?? new TemplateDefaults();
        var appSourceCop = template.AppSourceCop ?? new AppSourceCopSettings();

        return new TemplateSeed
        {
            Template = new TemplateMetaSeed
            {
                Key = template.Key,
                Runtime = template.Runtime,
                Name = template.Name,
                Description = template.Description,
                CoreIdRangeFrom = template.CoreIdRangeFrom,
                CoreIdRangeTo = template.CoreIdRangeTo,
                ModuleIdRangeStart = template.ModuleIdRangeStart,
                ModuleIdRangeSize = template.ModuleIdRangeSize,
                IsDefault = template.IsDefault,
                DefaultApplicationVersion = template.DefaultApplicationVersionLatest
                    ? ApplicationVersionService.LatestSentinel
                    : template.DefaultApplicationVersion?.Key,
                // Deleted modules are left out: their links are kept for a
                // restore (#1326), but the TOML editor refuses a key that
                // names no live module, so writing one here would make the
                // exported file impossible to paste back in.
                DefaultModules = template.DefaultModules
                    .OrderBy(d => d.Ordering)
                    .Where(d => d.Module is not null && d.Module.DeletedAt == null)
                    .Select(d => new TemplateDefaultModuleSeed { Key = d.Module!.Key })
                    .ToList(),
                IncludedFiles = template.IncludedFiles
                    .OrderBy(j => j.Ordering)
                    .Where(j => j.OrganizationFile is not null)
                    .Select(j => j.OrganizationFile!.Path)
                    .ToList(),
                RootFolders = template.RootFolders
                    .OrderBy(f => f.Ordering)
                    .Select(f => f.Path)
                    .ToList(),
            },
            Defaults = BuildDefaultsSeed(defaults),
            AppSourceCop = new AppSourceCopSeed
            {
                Include = appSourceCop.Include,
                MandatoryPrefix = appSourceCop.MandatoryPrefix,
                SupportedCountries = appSourceCop.SupportedCountries.ToList(),
            },
            // Extensions filled in manually after Tomlyn serialisation.
        };
    }

    private static DefaultsSeed BuildDefaultsSeed(TemplateDefaults defaults) => new()
    {
        Publisher = defaults.Publisher,
        Target = defaults.Target,
        Application = defaults.Application,
        Platform = defaults.Platform,
        Url = defaults.Url,
        Logo = defaults.Logo,
        Features = defaults.Features.ToList(),
        SupportedLocales = defaults.SupportedLocales.ToList(),
        Affix = defaults.Affix,
        AffixType = defaults.AffixType.ToString(),
        ResourceExposurePolicy = new ResourceExposurePolicySeed
        {
            AllowDebugging = defaults.ResourceExposurePolicy.AllowDebugging,
            AllowDownloadingSource = defaults.ResourceExposurePolicy.AllowDownloadingSource,
            IncludeSourceInSymbolFile = defaults.ResourceExposurePolicy.IncludeSourceInSymbolFile,
        },
    };

    private static void AppendExtension(StringBuilder sb, WorkspaceExtension ext)
    {
        AppendExtensionHeader(sb, ext.Path, ext.NameTemplate, ext.Required, ext.Application, ext.Runtime, ext.IdRangeFrom, ext.IdRangeTo);

        foreach (var dep in ext.Dependencies.OrderBy(d => d.Ordering))
        {
            AppendDependency(sb, dep.RefExtensionPath, dep.RefModuleKey, dep.LitId, dep.LitName, dep.LitPublisher, dep.LitVersion);
        }

        foreach (var folder in ext.Folders.OrderBy(f => f.Ordering))
        {
            AppendFolder(sb, folder, parentPath: "extensions");
        }
    }

    private static void AppendExtension(StringBuilder sb, ExtensionAuthoring ext)
    {
        AppendExtensionHeader(sb, ext.Path, ext.NameTemplate, ext.Required, ext.Application, ext.Runtime, ext.IdRangeFrom, ext.IdRangeTo);

        foreach (var dep in ext.Dependencies)
        {
            AppendDependency(sb, dep.RefExtensionPath, dep.RefModuleKey, dep.LitId, dep.LitName, dep.LitPublisher, dep.LitVersion);
        }

        foreach (var folder in ext.Folders)
        {
            AppendFolder(sb, folder, parentPath: "extensions");
        }
    }

    private static void AppendExtensionHeader(StringBuilder sb, string path, string nameTemplate, bool required, string? application, string? runtime, int? idRangeFrom, int? idRangeTo)
    {
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("[[extensions]]");
        sb.Append("path = ").AppendLine(TomlBasicString(path));
        sb.Append("name = ").AppendLine(TomlBasicString(nameTemplate));
        if (!required)
        {
            sb.AppendLine("required = false");
        }
        if (!string.IsNullOrEmpty(application))
        {
            sb.Append("application = ").AppendLine(TomlBasicString(application));
        }
        if (!string.IsNullOrEmpty(runtime))
        {
            sb.Append("runtime = ").AppendLine(TomlBasicString(runtime));
        }
        if (idRangeFrom is int from)
        {
            sb.Append("id_range_from = ").AppendLine(from.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (idRangeTo is int to)
        {
            sb.Append("id_range_to = ").AppendLine(to.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static void AppendDependency(StringBuilder sb, string? refExtensionPath, string? refModuleKey, string? litId, string? litName, string? litPublisher, string? litVersion)
    {
        sb.AppendLine();
        sb.AppendLine("[[extensions.dependencies]]");
        if (refExtensionPath is not null)
        {
            sb.Append("extension = ").AppendLine(TomlBasicString(refExtensionPath));
        }
        else if (refModuleKey is not null)
        {
            sb.Append("module = ").AppendLine(TomlBasicString(refModuleKey));
        }
        else
        {
            sb.Append("id = ").AppendLine(TomlBasicString(litId ?? string.Empty));
            sb.Append("name = ").AppendLine(TomlBasicString(litName ?? string.Empty));
            sb.Append("publisher = ").AppendLine(TomlBasicString(litPublisher ?? string.Empty));
            sb.Append("version = ").AppendLine(TomlBasicString(litVersion ?? string.Empty));
        }
    }

    /// <summary>
    /// Recursively emit a folder as <c>[[parentPath.folders]]</c>, its files
    /// as <c>[[parentPath.folders.files]]</c>, and child folders as
    /// <c>[[parentPath.folders.folders]]</c>. <paramref name="parentPath"/>
    /// is the TOML key prefix accumulating as we descend.
    /// </summary>
    private static void AppendFolder(StringBuilder sb, WorkspaceExtensionFolder folder, string parentPath)
    {
        var folderKey = $"{parentPath}.folders";
        sb.AppendLine();
        sb.Append("[[").Append(folderKey).AppendLine("]]");
        sb.Append("path = ").AppendLine(TomlBasicString(folder.Path));

        foreach (var file in folder.Files.OrderBy(f => f.Ordering))
        {
            AppendFile(sb, folderKey, file.Path, file.Content, file.IsExample);
        }

        foreach (var child in folder.Folders.OrderBy(f => f.Ordering))
        {
            AppendFolder(sb, child, folderKey);
        }
    }

    private static void AppendFolder(StringBuilder sb, FolderAuthoring folder, string parentPath)
    {
        var folderKey = $"{parentPath}.folders";
        sb.AppendLine();
        sb.Append("[[").Append(folderKey).AppendLine("]]");
        sb.Append("path = ").AppendLine(TomlBasicString(folder.Path));

        foreach (var file in folder.Files)
        {
            AppendFile(sb, folderKey, file.Path, file.Content, file.IsExample);
        }

        foreach (var child in folder.Folders)
        {
            AppendFolder(sb, child, folderKey);
        }
    }

    private static void AppendFile(StringBuilder sb, string folderKey, string path, string content, bool isExample)
    {
        sb.AppendLine();
        sb.Append("[[").Append(folderKey).AppendLine(".files]]");
        sb.Append("path = ").AppendLine(TomlBasicString(path));
        sb.Append("content = ").AppendLine(TomlMultilineBasic(content));
        if (isExample)
        {
            sb.AppendLine("is_example = true");
        }
    }

    // ===== FromToml =====

    /// <summary>
    /// Parses TOML into a <see cref="TemplateAuthoring"/> ready for downstream
    /// reconciliation. Throws <see cref="TomlParseException"/> with
    /// line-aware diagnostics on syntax errors.
    /// </summary>
    public static TemplateAuthoring FromToml(string toml, bool deprecated)
    {
        TemplateSeed seed;
        try
        {
            seed = TomlSerializer.Deserialize<TemplateSeed>(NormalizeRuntimeValue(toml), TomlOptions) ?? new TemplateSeed();
        }
        catch (TomlException tex)
        {
            var issues = tex.Diagnostics
                .Select(d => new TomlParseIssue(
                    Line: d.Span.Start.Line + 1,
                    Column: d.Span.Start.Column + 1,
                    Message: d.Message))
                .ToList();
            var summary = issues.Count == 0
                ? tex.Message
                : string.Join("; ", issues.Take(3).Select(i => $"line {i.Line}: {i.Message}"));
            throw new TomlParseException($"Failed to parse TOML: {summary}", issues, tex);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to parse TOML: {ex.Message}", ex);
        }

        // Materialise the strongly-typed value objects so we can serialise
        // them back through the same converter the structured form path uses.
        // affixType comes in as a string; tolerate case / whitespace and fall
        // back to None on anything unrecognised.
        var defaults = new TemplateDefaults
        {
            Publisher = seed.Defaults.Publisher,
            Target = seed.Defaults.Target,
            Application = seed.Defaults.Application,
            Platform = seed.Defaults.Platform,
            // seed.Defaults.ExtensionPrefix is deliberately not read: the prefix
            // moved onto organisation settings (#757). An older file carrying
            // the key still imports; the value is ignored.
            Url = seed.Defaults.Url,
            Logo = seed.Defaults.Logo,
            Features = seed.Defaults.Features.ToList(),
            SupportedLocales = seed.Defaults.SupportedLocales.ToList(),
            Affix = seed.Defaults.Affix,
            AffixType = ParseAffixType(seed.Defaults.AffixType),
            ResourceExposurePolicy = new ResourceExposurePolicy
            {
                AllowDebugging = seed.Defaults.ResourceExposurePolicy.AllowDebugging,
                AllowDownloadingSource = seed.Defaults.ResourceExposurePolicy.AllowDownloadingSource,
                IncludeSourceInSymbolFile = seed.Defaults.ResourceExposurePolicy.IncludeSourceInSymbolFile,
            },
        };

        var appSourceCop = new AppSourceCopSettings
        {
            Include = seed.AppSourceCop.Include,
            MandatoryPrefix = seed.AppSourceCop.MandatoryPrefix,
            SupportedCountries = seed.AppSourceCop.SupportedCountries.ToList(),
        };

        var extensions = seed.Extensions.Select(MapExtension).ToList();

        return new TemplateAuthoring(
            Key: seed.Template.Key,
            Runtime: seed.Template.Runtime,
            Name: seed.Template.Name,
            Description: seed.Template.Description,
            DefaultsJson: JsonSerializer.Serialize(defaults, JsonOptions),
            AppSourceCopJson: JsonSerializer.Serialize(appSourceCop, JsonOptions),
            CoreIdRangeFrom: seed.Template.CoreIdRangeFrom,
            CoreIdRangeTo: seed.Template.CoreIdRangeTo,
            ModuleIdRangeStart: seed.Template.ModuleIdRangeStart,
            ModuleIdRangeSize: seed.Template.ModuleIdRangeSize,
            Deprecated: deprecated,
            IsDefault: seed.Template.IsDefault,
            // The TOML carries either a fixed catalogue key or the
            // ApplicationVersionService.LatestSentinel ("latest"). The
            // sentinel lifts to the bool flag and clears the key; anything
            // else stays in the key slot for normal catalogue resolution.
            DefaultApplicationVersionKey: string.IsNullOrWhiteSpace(seed.Template.DefaultApplicationVersion)
                || string.Equals(seed.Template.DefaultApplicationVersion, ApplicationVersionService.LatestSentinel, StringComparison.Ordinal)
                ? null
                : seed.Template.DefaultApplicationVersion,
            DefaultModuleKeys: seed.Template.DefaultModules.Select(d => d.Key).ToList(),
            Extensions: extensions,
            CodeWorkspaceJson: string.IsNullOrWhiteSpace(seed.WorkspaceSettings?.Json)
                ? null
                : seed.WorkspaceSettings.Json,
            IncludedFilePaths: seed.Template.IncludedFiles.ToList(),
            RootFolderPaths: seed.Template.RootFolders.ToList(),
            DefaultApplicationVersionLatest: string.Equals(
                seed.Template.DefaultApplicationVersion,
                ApplicationVersionService.LatestSentinel,
                StringComparison.Ordinal));
    }

    private static ExtensionAuthoring MapExtension(ExtensionSeed seed) => new(
        Path: seed.Path,
        NameTemplate: seed.Name,
        Required: seed.Required,
        Application: string.IsNullOrEmpty(seed.Application) ? null : seed.Application,
        Runtime: string.IsNullOrEmpty(seed.Runtime) ? null : seed.Runtime,
        IdRangeFrom: seed.IdRangeFrom,
        IdRangeTo: seed.IdRangeTo,
        Folders: seed.Folders.Select(MapFolder).ToList(),
        Dependencies: seed.Dependencies.Select(MapDependency).ToList());

    private static FolderAuthoring MapFolder(FolderSeed seed) => new(
        Path: seed.Path,
        Folders: seed.Folders.Select(MapFolder).ToList(),
        Files: seed.Files.Select(f => new FileAuthoring(f.Path, f.Content, f.IsExample)).ToList());

    private static DependencyAuthoring MapDependency(DependencySeed seed)
    {
        // Tomlyn fills in all string properties even when absent (default "")
        // — collapse empty strings to null so the one-of contract holds.
        var ext = string.IsNullOrEmpty(seed.Extension) ? null : seed.Extension;
        var mod = string.IsNullOrEmpty(seed.Module) ? null : seed.Module;
        var litId = string.IsNullOrEmpty(seed.Id) ? null : seed.Id;
        return new DependencyAuthoring(
            RefExtensionPath: ext,
            RefModuleKey: mod,
            LitId: litId,
            LitName: string.IsNullOrEmpty(seed.Name) ? null : seed.Name,
            LitPublisher: string.IsNullOrEmpty(seed.Publisher) ? null : seed.Publisher,
            LitVersion: string.IsNullOrEmpty(seed.Version) ? null : seed.Version);
    }

    private static AffixType ParseAffixType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AffixType.None;
        return raw.Trim() switch
        {
            "Prefix" or "prefix" or "PREFIX" => AffixType.Prefix,
            "Suffix" or "suffix" or "SUFFIX" => AffixType.Suffix,
            _ => AffixType.None,
        };
    }

    // ===== Blank starter =====

    /// <summary>
    /// Starter TOML document for the New Template flow. Hand-written rather
    /// than serialised so the comments survive — admins authoring their first
    /// template see annotated sections for every common knob: defaults that
    /// merge into <c>app.json</c>, the <c>[appSourceCop]</c> block, a single
    /// required <c>Core</c> extension with a nested folder + example file,
    /// and commented-out scaffolding for optional extensions and each
    /// dependency-reference shape. Comments are dropped by Tomlyn on parse,
    /// so they live only in the textarea — saving round-trips through
    /// <see cref="ToToml"/> which re-emits without them.
    /// </summary>
    public static string BlankToml() => """"
# AL Workbench workspace template — TOML reference.
# Edit the fields below, then click "Create template". Comments are
# stripped on save and won't reappear after the next round-trip.
# Anything you leave at its default stays empty in the generated
# app.json / AppSourceCop.json.

[template]
# Stable identifier shown in URLs and used for cross-org imports. Pick
# once — renaming breaks references from imported templates. Lowercase
# letters, digits, and hyphens only.
key = ""

# Business Central runtime version. "26" (major) or "26.1" (Major.Minor).
runtime = "26"

# Display name in the templates browser. Required.
name = ""
description = ""

# Object-ID ranges. The Core range is used by the template's primary
# extension. Modules are auto-allocated module_id_range_size IDs each
# from a sliding window starting at module_id_range_start.
core_id_range_from = 90000
core_id_range_to = 90999
module_id_range_start = 91000
module_id_range_size = 200

# Optional key from /admin/application-versions. When set, the New
# Workspace form snaps application + runtime together to this entry.
# default_application_version = ""

# Folders every generated workspace gets at its top level, with nothing
# in them - handy for a folder your tools fill in later. Use "/" to nest.
# Uncomment and edit, or delete.
# root_folders = [".alpackages"]

[defaults]
# Merged verbatim into every generated app.json; some fields also
# pre-fill the New Workspace form.

# AL "publisher" field written into every generated app.json. Set this
# before users generate workspaces — there's no per-workspace override.
publisher = ""

# "OnPrem" or "Cloud".
target = "Cloud"

# Pre-fills the application / platform inputs on New Workspace. Match
# the BC version you ship against; per-extension overrides on
# [[extensions]] handle mixed-version setups.
application = "26.0.0.0"
platform = "1.0.0.0"

# Optional URL written into every app.json.
# url = "https://example.com"

# AL feature flags. Common picks: "TranslationFile", "NoImplicitWith".
features = ["TranslationFile"]
supportedLocales = ["en-US"]

# AL object-name affix substituted into {{affix}} placeholders in .al
# files. With affixType = "None" the placeholder collapses to empty,
# regardless of `affix`. Set affixType to "Prefix" or "Suffix" to
# enable substitution.
affix = ""
affixType = "None"

[defaults.resourceExposurePolicy]
allowDebugging = false
allowDownloadingSource = false
includeSourceInSymbolFile = false

[appSourceCop]
# Set include = false to suppress AppSourceCop.json in every generated
# extension (useful for non-AppSource templates). When true, the
# remaining fields are written verbatim into each extension's
# AppSourceCop.json — set mandatoryPrefix to your AL object-name prefix
# when shipping to AppSource; leave both empty otherwise.
include = true
mandatoryPrefix = ""
supportedCountries = ["US"]

# Each [[extensions]] entry becomes its own folder in the generated
# workspace, with its own app.json + AppSourceCop.json. Order matters:
# the first required extension is the scaffold for standalone-extension
# generation.
[[extensions]]
path = "Core"
name = "{{extension_prefix}} Core"
required = true

# Optional per-extension overrides — only set them when this extension
# needs a different BC version from the template-wide defaults.
# application = "26.1.0.0"
# runtime = "26.1"

# Explicit id-range override. Without these two, the generator
# allocates from the template's Core range (extension #0) or a sliding
# module window (extension #1+).
# id_range_from = 50100
# id_range_to = 50199

# Each [[extensions.dependencies]] entry references exactly one of:
#   - another [[extensions]] in this template (extension = "Path"),
#   - a Module from the catalogue (module = "Key"),
#   - a literal AL app (id / name / publisher / version).
# Examples below are commented out — uncomment and edit, or delete.
#
# [[extensions.dependencies]]
# extension = "Core"
#
# [[extensions.dependencies]]
# module = "shared-lib"
#
# [[extensions.dependencies]]
# id = "63ca2fa4-4f03-4f2b-a480-172fef340d3f"
# name = "Base Application"
# publisher = "Microsoft"
# version = "26.0.0.0"

# Folder tree under this extension. Nest via [[extensions.folders.folders]];
# files attach at any depth with [[...folders.files]]. Empty leaves ship
# a .gitkeep so the structure survives the ZIP round-trip.
[[extensions.folders]]
path = "src"

[[extensions.folders.folders]]
path = "codeunits"

[[extensions.folders.folders.files]]
path = "Hello.al"
content = """
codeunit 50000 "{{affix}}Hello"
{
    trigger OnRun()
    begin
        Message('Hello from {{name}}');
    end;
}
"""
# Example files only ship to the user's ZIP when "include examples" is
# ticked on New Workspace. Flip to true for files you want hidden by
# default.
is_example = false

[[extensions.folders]]
path = "permissionsets"

[[extensions.folders]]
path = "translations"

# An additional opt-in extension. Required = false surfaces a checkbox
# on New Workspace; users tick to include this extension's folder in
# the generated workspace. Uncomment and edit, or delete.
#
# [[extensions]]
# path = "Hotfix"
# name = "{{extension_prefix}} Hotfix"
# required = false
#
# [[extensions.folders]]
# path = "src"

# Catalogue modules pre-selected on New Workspace when this template is
# picked. The user can untick them. Keys must exist in /admin/modules.
#
# [[template.default_modules]]
# key = "shared-lib"
"""";

    // ===== Runtime normalisation =====

    private static readonly Regex UnquotedRuntimeRegex =
        new(@"(?m)^(\s*runtime\s*=\s*)(\d+(?:\.\d+)?)\s*(#.*)?$", RegexOptions.Compiled);

    /// <summary>
    /// Wraps bare <c>runtime = 15</c> values in quotes so Tomlyn can
    /// deserialise them into the seed's string runtime field. Carried over
    /// from the pre-unified mapper.
    /// </summary>
    public static string NormalizeRuntimeValue(string toml)
    {
        if (string.IsNullOrEmpty(toml)) return toml;
        return UnquotedRuntimeRegex.Replace(toml, m =>
        {
            var prefix = m.Groups[1].Value;
            var value = m.Groups[2].Value;
            var trailing = m.Groups[3].Success ? " " + m.Groups[3].Value : string.Empty;
            return $"{prefix}\"{value}\"{trailing}";
        });
    }

    // ===== TOML string helpers =====

    /// <summary>
    /// Empty-arrays line that Tomlyn emits for the (intentionally empty)
    /// <c>extensions</c> field on the header seed. Stripped before we append
    /// the hand-emitted <c>[[extensions]]</c> blocks.
    /// </summary>
    private static readonly Regex EmptyExtensionsLineRegex =
        new(@"(?m)^extensions\s*=\s*\[\s*\]\s*\r?\n?", RegexOptions.Compiled);

    private static string TomlBasicString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string TomlMultilineBasic(string content)
    {
        var escaped = content
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"\"\"", "\\\"\\\"\\\"", StringComparison.Ordinal);
        return $"\"\"\"\n{escaped}\"\"\"";
    }
}
