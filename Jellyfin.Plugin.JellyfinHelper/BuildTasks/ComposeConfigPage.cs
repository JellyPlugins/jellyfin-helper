using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Jellyfin.Plugin.JellyfinHelper.BuildTasks;

public class ComposeConfigPage : Task
{
    private static readonly char[] Separator = [';'];

    [Required]
    public string TemplateFile { get; set; } = string.Empty;

    [Required]
    public string CssFiles { get; set; } = string.Empty;

    [Required]
    public string JsFiles { get; set; } = string.Empty;

    [Required]
    public string OutputFile { get; set; } = string.Empty;

    public override bool Execute()
    {
        try
        {
            var template = File.ReadAllText(TemplateFile);

            var cssList = CssFiles.Split(Separator, StringSplitOptions.RemoveEmptyEntries)
                .Select(cssFile => cssFile.Trim())
                .Where(cssFile => !string.IsNullOrEmpty(cssFile))
                .ToList();
            var jsList = JsFiles.Split(Separator, StringSplitOptions.RemoveEmptyEntries)
                .Select(jsFile => jsFile.Trim())
                .Where(jsFile => !string.IsNullOrEmpty(jsFile))
                .ToList();

            if (!ValidateModuleOrder(cssList, jsList))
            {
                return false;
            }

            var cssBuilder = new StringBuilder();
            foreach (var trimmed in cssList)
            {
                if (!File.Exists(trimmed))
                {
                    Log.LogError("Configured CSS module was not found: {0}", trimmed);
                    return false;
                }

                cssBuilder.AppendLine(File.ReadAllText(trimmed));
            }

            var jsBuilder = new StringBuilder();
            jsBuilder.AppendLine("(function () {");
            jsBuilder.AppendLine("'use strict';");

            foreach (var trimmed in jsList)
            {
                if (!File.Exists(trimmed))
                {
                    Log.LogError("Configured JS module was not found: {0}", trimmed);
                    return false;
                }

                jsBuilder.AppendLine(File.ReadAllText(trimmed));
            }

            jsBuilder.AppendLine("})();");

            var hasPlaceholderErrors = false;
            if (!template.Contains("/* CSS_CONTENT */"))
            {
                Log.LogError("Template does not contain /* CSS_CONTENT */ placeholder");
                hasPlaceholderErrors = true;
            }

            if (!template.Contains("/* JS_CONTENT */"))
            {
                Log.LogError("Template does not contain /* JS_CONTENT */ placeholder");
                hasPlaceholderErrors = true;
            }

            if (hasPlaceholderErrors)
            {
                return false;
            }

            var result = template
                .Replace("/* CSS_CONTENT */", cssBuilder.ToString())
                .Replace("/* JS_CONTENT */", jsBuilder.ToString());

            var dir = Path.GetDirectoryName(OutputFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(OutputFile, result);
            Log.LogMessage(MessageImportance.High, "Composed configPage.html from template + CSS modules + JS modules");

            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Log.LogErrorFromException(ex, showStackTrace: false);
            return false;
        }
    }

    // The composed IIFE depends on a fixed module order that nothing else enforces at build time.
    // Shared must load first so its helpers exist before any tab uses them, and Main must load
    // last because it closes the IIFE and wires up tab routing. A silent reorder in the csproj
    // would still concatenate cleanly but produce a broken page, so we fail the build here instead.
    private bool ValidateModuleOrder(System.Collections.Generic.List<string> cssList, System.Collections.Generic.List<string> jsList)
    {
        var valid = true;

        if (cssList.Count == 0)
        {
            Log.LogError("No CSS modules were configured; Shared.css must be present and first");
            valid = false;
        }
        else if (!IsFile(cssList[0], "Shared.css"))
        {
            Log.LogError("First CSS module must be Shared.css but was {0}. CSS order: {1}", Path.GetFileName(cssList[0]), FileNames(cssList));
            valid = false;
        }

        if (jsList.Count == 0)
        {
            Log.LogError("No JS modules were configured; Shared.js must be first and Main.js last");
            return false;
        }

        if (!IsFile(jsList[0], "Shared.js"))
        {
            Log.LogError("First JS module must be Shared.js but was {0}. JS order: {1}", Path.GetFileName(jsList[0]), FileNames(jsList));
            valid = false;
        }

        var mainIndex = jsList.FindIndex(js => IsFile(js, "Main.js"));
        if (mainIndex < 0)
        {
            Log.LogError("Main.js is missing from the JS modules; it closes the IIFE and must be last. JS order: {0}", FileNames(jsList));
            valid = false;
        }
        else if (mainIndex != jsList.Count - 1)
        {
            Log.LogError("Main.js must be the last JS module but was at position {0} of {1}. JS order: {2}", mainIndex + 1, jsList.Count, FileNames(jsList));
            valid = false;
        }

        return valid;
    }

    private static bool IsFile(string path, string fileName)
        => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase);

    private static string FileNames(System.Collections.Generic.List<string> paths)
        => string.Join(", ", paths.Select(Path.GetFileName));
}