using System;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Kaeo.LlmProxy.VSExtension.Core
{
    /// <summary>Outcome of a surgical project-file edit. Plain class (netstandard2.0 has no records).</summary>
    public sealed class ProjectFileEditResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? NewXml { get; set; }
        public string? ChangeDescription { get; set; }

        public static ProjectFileEditResult Fail(string error)
        {
            return new ProjectFileEditResult { Success = false, Error = error };
        }

        public static ProjectFileEditResult Ok(string newXml, string description)
        {
            return new ProjectFileEditResult { Success = true, NewXml = newXml, ChangeDescription = description };
        }
    }

    /// <summary>
    /// Pure, formatting-preserving edits of SDK-style project files. Loads with
    /// <see cref="LoadOptions.PreserveWhitespace"/> and saves with
    /// <see cref="SaveOptions.DisableFormatting"/> so indentation, blank lines, comments, and the
    /// original newline style survive untouched except for the targeted node. Namespace-agnostic
    /// (matches by local name) so it also reads classic projects for the SDK-style check.
    /// </summary>
    public static class ProjectFileEditor
    {
        /// <summary>
        /// True when the project is SDK-style: a &lt;Project Sdk="..."&gt; attribute, a child
        /// &lt;Sdk&gt; element, or an &lt;Import Sdk="..."&gt;. Classic projects (ToolsVersion +
        /// MSBuild xmlns, packages.config) return false and must be refused by the caller.
        /// </summary>
        public static bool IsSdkStyle(string xml)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            }
            catch (Exception)
            {
                return false;
            }
            var root = doc.Root;
            if (root == null || root.Name.LocalName != "Project")
                return false;
            if (root.Attribute("Sdk") != null)
                return true;
            if (root.Elements().Any(e => e.Name.LocalName == "Sdk"))
                return true;
            return root.Elements().Any(e => e.Name.LocalName == "Import" && e.Attribute("Sdk") != null);
        }

        /// <summary>
        /// Sets the target framework. A value containing ';' targets multiple frameworks and uses
        /// &lt;TargetFrameworks&gt;; a single value uses &lt;TargetFramework&gt;. Renames the element
        /// when the cardinality changes, replaces the value in place otherwise, and adds the element
        /// (creating a PropertyGroup if needed) when absent. Formatting/comments/newlines preserved.
        /// </summary>
        public static ProjectFileEditResult SetTargetFramework(string xml, string framework)
        {
            if (string.IsNullOrWhiteSpace(framework))
                return ProjectFileEditResult.Fail("framework is required.");
            framework = framework.Trim();

            XDocument doc;
            try
            {
                doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            }
            catch (Exception ex)
            {
                return ProjectFileEditResult.Fail("Project file is not valid XML: " + ex.Message);
            }
            var root = doc.Root;
            if (root == null || root.Name.LocalName != "Project")
                return ProjectFileEditResult.Fail("Root element is not <Project>.");

            var isMulti = framework.IndexOf(';') >= 0;
            var desiredName = isMulti ? "TargetFrameworks" : "TargetFramework";
            var ns = root.GetDefaultNamespace();
            var newline = xml.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";

            // Locate an existing TFM element in any PropertyGroup.
            XElement? existing = null;
            foreach (var pg in root.Elements().Where(e => e.Name.LocalName == "PropertyGroup"))
            {
                existing = pg.Elements().FirstOrDefault(e =>
                    e.Name.LocalName == "TargetFramework" || e.Name.LocalName == "TargetFrameworks");
                if (existing != null)
                    break;
            }

            string description;
            if (existing != null)
            {
                var oldName = existing.Name.LocalName;
                var oldValue = existing.Value;
                if (oldName == desiredName)
                {
                    existing.Value = framework;
                    description = "Updated <" + desiredName + "> from '" + oldValue + "' to '" + framework + "'.";
                }
                else
                {
                    var replacement = new XElement(existing.Name.Namespace + desiredName, framework);
                    existing.ReplaceWith(replacement);
                    description = "Replaced <" + oldName + "> ('" + oldValue + "') with <" + desiredName + "> ('" + framework + "').";
                }
            }
            else
            {
                var pg = root.Elements().FirstOrDefault(e => e.Name.LocalName == "PropertyGroup");
                var newElem = new XElement(ns + desiredName, framework);
                if (pg != null)
                {
                    AddToPropertyGroup(pg, newElem, newline);
                    description = "Added <" + desiredName + "> ('" + framework + "') to an existing PropertyGroup.";
                }
                else
                {
                    var baseIndent = IndentBefore(root.Elements().FirstOrDefault(), "  ");
                    var newPg = new XElement(ns + "PropertyGroup",
                        new XText(newline + baseIndent + "  "),
                        newElem,
                        new XText(newline + baseIndent));
                    var firstChild = root.Nodes().FirstOrDefault(n => n is XElement);
                    if (firstChild != null)
                        firstChild.AddBeforeSelf(newPg, new XText(newline + baseIndent));
                    else
                        root.Add(new XText(newline + baseIndent), newPg, new XText(newline));
                    description = "Created a PropertyGroup with <" + desiredName + "> ('" + framework + "').";
                }
            }

            var newXml = SerializeVerbatim(doc, newline);
            return ProjectFileEditResult.Ok(newXml, description);
        }

        /// <summary>
        /// Serializes without reformatting, then restores the source's dominant newline. The XML
        /// parser normalizes CRLF/CR to LF at load time (XML spec), so whitespace nodes are LF
        /// regardless of input; we write uniformly to LF (NewLineChars="\n") and convert back to
        /// CRLF when the source used it. Indentation, blank lines, and comments survive because
        /// PreserveWhitespace kept every text node. The declaration is re-attached with the source
        /// newline because the writer omits it.
        /// </summary>
        private static string SerializeVerbatim(XDocument doc, string newline)
        {
            var sb = new StringBuilder();
            var settings = new XmlWriterSettings
            {
                Indent = false,
                NewLineHandling = NewLineHandling.Replace,
                NewLineChars = "\n",
                OmitXmlDeclaration = true,
                ConformanceLevel = ConformanceLevel.Document,
            };
            using (var writer = XmlWriter.Create(sb, settings))
            {
                doc.Save(writer);
            }
            var body = sb.ToString();
            if (newline == "\r\n")
                body = body.Replace("\n", "\r\n");
            // The body already carries any whitespace that separated the declaration from the root
            // (PreserveWhitespace keeps it as a document-level text node), so don't add another.
            return doc.Declaration != null
                ? doc.Declaration + body
                : body;
        }

        /// <summary>
        /// Inserts <paramref name="newElem"/> as the last element child of the PropertyGroup,
        /// matching the indentation of existing children and keeping the closing-tag newline.
        /// </summary>
        private static void AddToPropertyGroup(XElement pg, XElement newElem, string newline)
        {
            var lastElement = pg.Elements().LastOrDefault();
            var childIndent = lastElement != null
                ? IndentBefore(lastElement, "    ")
                : IndentBefore(pg, "  ") + "  ";

            if (lastElement != null && pg.LastNode is XText trailingText)
            {
                // Insert before the whitespace that precedes </PropertyGroup>.
                trailingText.AddBeforeSelf(new XText(newline + childIndent), newElem);
            }
            else if (lastElement != null)
            {
                lastElement.AddAfterSelf(new XText(newline + childIndent), newElem, new XText(newline));
            }
            else
            {
                // Empty PropertyGroup: build its content with matching indentation.
                var baseIndent = IndentBefore(pg, "  ");
                pg.Add(new XText(newline + childIndent), newElem, new XText(newline + baseIndent));
            }
        }

        /// <summary>
        /// The whitespace indent preceding a node (text after the last newline of its previous
        /// whitespace text node), or <paramref name="fallback"/> when it cannot be determined.
        /// </summary>
        private static string IndentBefore(XNode? node, string fallback)
        {
            if (node == null)
                return fallback;
            if (node.PreviousNode is not XText prev)
                return fallback;
            var value = prev.Value;
            var nl = value.LastIndexOf('\n');
            if (nl < 0)
                return fallback;
            var indent = value.Substring(nl + 1);
            return indent.Length > 0 && indent.Trim().Length == 0 ? indent : fallback;
        }
    }
}