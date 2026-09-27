using Microsoft.AspNetCore.Http;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Qemik.Core;

public sealed partial class LocalFolderSharing
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly string[] Properties = ["displayname", "resourcetype", "getcontentlength", "getcontenttype", "getlastmodified", "creationdate", "getetag"];
    private static void EnsureNoLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = System.IO.Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Linked folders and reparse points cannot be shared.");
    }
    private static string Resolve(Folder folder, IEnumerable<string> segments)
    {
        var parts = segments.ToArray();
        if (parts.Any(s => s is "." or ".." || s.EndsWith('.') || s.EndsWith(' ') || s.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || s.Contains('\\') || s.Contains(':') || System.Text.RegularExpressions.Regex.IsMatch(s, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new UnauthorizedAccessException("Invalid shared path.");
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine([folder.Path, .. parts]));
        var rootPath = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder.Path));
        var prefix = System.IO.Path.EndsInDirectorySeparator(rootPath) ? rootPath : rootPath + System.IO.Path.DirectorySeparatorChar;
        if (!path.Equals(rootPath, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
        EnsureNoLinks(path); return path;
    }
    private static string Etag(FileSystemInfo item) => $"\"{item.LastWriteTimeUtc.Ticks:x}-{(item is FileInfo f ? f.Length : 0):x}\"";
    private async Task HandleAsync(HttpContext context)
    {
        // No browser-origin requests or arbitrary DNS hosts. Tokens scope access to one selected folder.
        var request = context.Request; var response = context.Response;
        if (request.Headers.ContainsKey("Origin") || request.Host.Host is not ("127.0.0.1" or "10.0.2.2")) { response.StatusCode = 403; return; }
        if (request.Path == $"/{toolsToken}/display-fit.py" && request.Method == "GET")
        {
            response.ContentType = "text/x-python; charset=utf-8";
            using var script = typeof(LocalFolderSharing).Assembly.GetManifestResourceStream("Qemik.Core.GuestTools.display-fit.py")!;
            await script.CopyToAsync(response.Body, context.RequestAborted); return;
        }
        await files.WaitAsync(context.RequestAborted);
        try
        {
            var parts = (request.Path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
            await gate.WaitAsync(context.RequestAborted);
            Folder? folder;
            try { folder = parts.Length >= 2 ? folders.SingleOrDefault(f => f.Token == parts[0] && f.Name == parts[1]) : null; }
            finally { gate.Release(); }
            if (folder is null) { response.StatusCode = 404; return; }
            var path = Resolve(folder, parts.Skip(2));
            var isRoot = parts.Length == 2;
            var exists = File.Exists(path) || Directory.Exists(path);
            var method = request.Method;
            response.Headers.CacheControl = "no-store";
            response.Headers["DAV"] = "1";
            response.Headers.Allow = folder.ReadOnly ? "OPTIONS, PROPFIND, GET, HEAD" : "OPTIONS, PROPFIND, GET, HEAD, PUT, MKCOL, DELETE, MOVE, COPY";
            if (method == "OPTIONS") { response.StatusCode = 200; return; }
            if (method is not ("GET" or "HEAD" or "PROPFIND") && folder.ReadOnly) { response.StatusCode = 403; return; }
            if (method is "PROPFIND")
            {
                if (!exists) { response.StatusCode = 404; return; }
                var depth = request.Headers["Depth"].ToString();
                if (depth is not ("0" or "1")) { response.StatusCode = 403; return; }
                var names = Properties.Select(p => Dav + p).ToList();
                if (request.ContentLength > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
                {
                    using var reader = XmlReader.Create(request.Body, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 65536, XmlResolver = null });
                    var doc = await XDocument.LoadAsync(reader, LoadOptions.None, context.RequestAborted);
                    if (doc.Root?.Element(Dav + "prop") is { } prop) names = prop.Elements().Select(e => e.Name).ToList();
                }
                var items = new List<FileSystemInfo> { Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path) };
                if (depth == "1" && Directory.Exists(path)) items.AddRange(new DirectoryInfo(path).EnumerateFileSystemInfos().Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 && !f.Name.StartsWith(".qemik-upload-", StringComparison.Ordinal)).Take(10001));
                if (items.Count > 10001) { response.StatusCode = 507; return; }
                var xml = new XElement(Dav + "multistatus", items.Select(item => Describe(folder, item, names)));
                response.StatusCode = 207; response.ContentType = "application/xml; charset=utf-8";
                await response.WriteAsync(xml.ToString(SaveOptions.DisableFormatting), context.RequestAborted); return;
            }
            if (method is "GET" or "HEAD")
            {
                if (!File.Exists(path)) { response.StatusCode = exists ? 405 : 404; return; }
                var info = new FileInfo(path);
                await Results.File(path, "application/octet-stream", lastModified: info.LastWriteTimeUtc, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(Etag(info)), enableRangeProcessing: true).ExecuteAsync(context); return;
            }
            if (isRoot) { response.StatusCode = 403; return; }
            if (request.Headers.IfNoneMatch == "*" && exists) { response.StatusCode = 412; return; }
            if (request.Headers.IfMatch.Count > 0 && (!exists || request.Headers.IfMatch != "*" && request.Headers.IfMatch != Etag(Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path)))) { response.StatusCode = 412; return; }
            if (method == "PUT")
            {
                if (Directory.Exists(path) || !Directory.Exists(System.IO.Path.GetDirectoryName(path))) { response.StatusCode = 409; return; }
                var temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, ".qemik-upload-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true)) await request.Body.CopyToAsync(file, context.RequestAborted);
                    Resolve(folder, parts.Skip(2)); File.Move(temp, path, true); response.StatusCode = exists ? 204 : 201;
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                return;
            }
            if (method == "MKCOL")
            {
                if (exists) { response.StatusCode = 405; return; }
                if (request.ContentLength > 0) { response.StatusCode = 415; return; }
                if (!Directory.Exists(System.IO.Path.GetDirectoryName(path))) { response.StatusCode = 409; return; }
                Directory.CreateDirectory(path); response.StatusCode = 201; return;
            }
            if (method == "DELETE")
            {
                if (!exists) { response.StatusCode = 404; return; }
                if (Directory.Exists(path)) { CheckTree(path); Directory.Delete(path, true); } else File.Delete(path);
                response.StatusCode = 204; return;
            }
            if (method is "MOVE" or "COPY")
            {
                if (!exists) { response.StatusCode = 404; return; }
                if (!Uri.TryCreate(request.Headers["Destination"], UriKind.Absolute, out var destination) || destination.Scheme is not ("http" or "dav") || destination.Port != port || destination.Host != request.Host.Host) { response.StatusCode = 403; return; }
                var targetParts = destination.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
                if (targetParts.Length < 3 || targetParts[0] != folder.Token || targetParts[1] != folder.Name) { response.StatusCode = 403; return; }
                var target = Resolve(folder, targetParts.Skip(2));
                if (target.Equals(path, StringComparison.OrdinalIgnoreCase) || target.StartsWith(path + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { response.StatusCode = 403; return; }
                if (!Directory.Exists(System.IO.Path.GetDirectoryName(target))) { response.StatusCode = 409; return; }
                var targetExists = File.Exists(target) || Directory.Exists(target);
                if (targetExists && request.Headers["Overwrite"] == "F") { response.StatusCode = 412; return; }
                // Never destructively replace a whole destination directory.
                if (Directory.Exists(target) || Directory.Exists(path) && File.Exists(target)) { response.StatusCode = 409; return; }
                if (Directory.Exists(path))
                {
                    CheckTree(path);
                    if (method == "MOVE") Directory.Move(path, target);
                    else CopyTree(path, target, request.Headers["Depth"] != "0", context.RequestAborted);
                }
                else if (method == "MOVE") File.Move(path, target, true);
                else File.Copy(path, target, true);
                response.StatusCode = targetExists ? 204 : 201; return;
            }
            response.StatusCode = 405;
        }
        catch (UnauthorizedAccessException) { if (!response.HasStarted) response.StatusCode = 403; }
        catch (FileNotFoundException) { if (!response.HasStarted) response.StatusCode = 404; }
        catch (DirectoryNotFoundException) { if (!response.HasStarted) response.StatusCode = 404; }
        catch (XmlException) { if (!response.HasStarted) response.StatusCode = 400; }
        catch (IOException) { if (!response.HasStarted) response.StatusCode = 409; }
        finally { files.Release(); }
    }
    private static void CheckTree(string path)
    {
        EnsureNoLinks(path);
        foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException();
            if (item is DirectoryInfo) CheckTree(item.FullName);
        }
    }
    private static void CopyTree(string source, string destination, bool recursive, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); EnsureNoLinks(source); EnsureNoLinks(destination);
        Directory.CreateDirectory(destination);
        if (!recursive) return;
        foreach (var item in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested(); EnsureNoLinks(item.FullName);
            var target = System.IO.Path.Combine(destination, item.Name);
            if (item is DirectoryInfo) CopyTree(item.FullName, target, true, ct);
            else File.Copy(item.FullName, target, false);
        }
    }
    private static XElement Describe(Folder folder, FileSystemInfo item, List<XName> names)
    {
        var directory = item is DirectoryInfo;
        var relative = System.IO.Path.GetRelativePath(folder.Path, item.FullName);
        var href = "/" + folder.Token + "/" + Uri.EscapeDataString(folder.Name) + "/" + (relative == "." ? "" : string.Join('/', relative.Split(System.IO.Path.DirectorySeparatorChar).Select(Uri.EscapeDataString)) + (directory ? "/" : ""));
        var found = new XElement(Dav + "prop"); var missing = new XElement(Dav + "prop");
        foreach (var name in names)
        {
            if (name.Namespace != Dav || !Properties.Contains(name.LocalName)) { missing.Add(new XElement(name)); continue; }
            object value = name.LocalName switch
            {
                "displayname" => relative == "." ? folder.Name : item.Name,
                "resourcetype" => directory ? new XElement(Dav + "collection") : "",
                "getcontentlength" => directory ? "0" : ((FileInfo)item).Length.ToString(CultureInfo.InvariantCulture),
                "getcontenttype" => directory ? "httpd/unix-directory" : "application/octet-stream",
                "getlastmodified" => item.LastWriteTimeUtc.ToString("R", CultureInfo.InvariantCulture),
                "creationdate" => item.CreationTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                _ => Etag(item)
            };
            found.Add(new XElement(name, value));
        }
        var response = new XElement(Dav + "response", new XElement(Dav + "href", href));
        if (found.HasElements) response.Add(new XElement(Dav + "propstat", found, new XElement(Dav + "status", "HTTP/1.1 200 OK")));
        if (missing.HasElements) response.Add(new XElement(Dav + "propstat", missing, new XElement(Dav + "status", "HTTP/1.1 404 Not Found")));
        return response;
    }
}
