﻿//-----------------------------------------------------------------------
// <copyright file="GitUtilities.cs" company="(none)">
//  Copyright © 2013 John Gietzen and the WebGit .NET Authors. All rights reserved.
// </copyright>
// <author>John Gietzen</author>
//-----------------------------------------------------------------------

namespace WebGitNet
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using System.Web;
    using System.Web.Configuration;
    using StackExchange.Profiling;

    public static class GitUtilities
    {
        public static Encoding DefaultEncoding
        {
            get { return Encoding.GetEncoding(28591); }
        }

        public static string EmptyTreeHash
        {
            get { return "4b825dc642cb6eb9a060e54bf8d69288fbee4904"; }
        }

        public static int CountCommits(string repoPath, string @object = null, bool allRefs = false)
        {
            @object = @object ?? "HEAD";
            var results = Execute(string.Format("shortlog -s{0} {1}", allRefs ? " --all" : "", Q(@object)), repoPath);
            return (from r in results.Split("\n".ToArray(), StringSplitOptions.RemoveEmptyEntries)
                    let count = r.Split("\t".ToArray(), StringSplitOptions.RemoveEmptyEntries)[0]
                    select int.Parse(count.Trim())).Sum();
        }

        public static void CreateRepo(string repoPath)
        {
            var workingDir = Path.GetDirectoryName(repoPath);
            var results = Execute(string.Format("init --bare {0}", Q(repoPath)), workingDir, trustErrorCode: true);
        }

        public static string Execute(string command, string workingDir, Encoding outputEncoding = null, bool trustErrorCode = false)
        {
            using (var git = Start(command, workingDir, redirectInput: false, redirectError: trustErrorCode, outputEncoding: outputEncoding))
            {
                Task<string> readErrorTask = null;
                if (trustErrorCode)
                {
                    readErrorTask = new Task<string>(() => git.StandardError.ReadToEnd());
                    readErrorTask.Start();
                }

                var result = git.StandardOutput.ReadToEnd();
                git.WaitForExit();

                if (trustErrorCode)
                {
                    readErrorTask.Wait();

                    if (git.ExitCode != 0)
                    {
                        throw new GitErrorException(command, workingDir, git.ExitCode, readErrorTask.Result);
                    }
                }

                return result;
            }
        }

        public static void ExecutePostCreateHook(string repoPath)
        {
            var sh = WebConfigurationManager.AppSettings["ShCommand"];

            // If 'sh.exe' is not configured, derive the path relative to the git.exe command path.
            if (string.IsNullOrEmpty(sh))
            {
                var git = WebConfigurationManager.AppSettings["GitCommand"];
                sh = Path.Combine(Path.GetDirectoryName(git), "sh.exe");
            }

            // Find the path of the post-create hook.
            var repositories = WebConfigurationManager.AppSettings["RepositoriesPath"];
            var hookRelativePath = WebConfigurationManager.AppSettings["PostCreateHook"];

            // If the hook path is not configured, default to a path of "post-create", relative to the repository directory.
            if (string.IsNullOrEmpty(hookRelativePath))
            {
                hookRelativePath = "post-create";
            }

            // Get the full path info for the hook file, and ensure that it exists.
            var hookFile = new FileInfo(Path.Combine(repositories, hookRelativePath));
            if (!hookFile.Exists)
            {
                return;
            }

            // Prepare to start sh.exe like: `sh.exe -- "C:\Path\To\Hook-Script"`.
            var startInfo = new ProcessStartInfo(sh, string.Format("-- {0}", Q(hookFile.FullName)))
            {
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.EnvironmentVariables["PATH"] = Environment.GetEnvironmentVariable("PATH") + Path.PathSeparator + Path.GetDirectoryName(sh);

            // Start the script and wait for exit.
            using (var script = Process.Start(startInfo))
            {
                script.WaitForExit();
            }
        }

        public static List<GitRef> GetAllRefs(string repoPath)
        {
            var result = Execute("show-ref", repoPath);
            return (from l in result.Split("\n".ToArray(), StringSplitOptions.RemoveEmptyEntries)
                    let parts = l.Split(' ')
                    select new GitRef(parts[0], parts[1])).ToList();
        }

        public static MemoryStream GetBlob(string repoPath, string tree, string path)
        {
            MemoryStream blob = null;
            try
            {
                blob = new MemoryStream();
                using (var git = StartGetBlob(repoPath, tree, path))
                {
                    var buffer = new byte[1048576];
                    var readCount = 0;
                    while ((readCount = git.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        blob.Write(buffer, 0, readCount);
                    }
                }

                blob.Seek(0, SeekOrigin.Begin);

                var tempBlob = blob;
                blob = null;
                return tempBlob;
            }
            finally
            {
                if (blob != null)
                {
                    blob.Dispose();
                }
            }
        }

        public static List<DiffInfo> GetDiffInfo(string repoPath, string parentCommit, string childCommit)
        {
            var diffs = new List<DiffInfo>();
            List<string> diffLines = null;

            Action addLastDiff = () =>
            {
                if (diffLines != null)
                {
                    diffs.Add(new DiffInfo(diffLines));
                }
            };

            using (var git = Start(string.Format("diff-tree --patch -r --raw --src-prefix=\"a:/\" --dst-prefix=\"b:/\" {0} {1}", Q(parentCommit), Q(childCommit)), repoPath))
            {
                while (!git.StandardOutput.EndOfStream)
                {
                    var line = git.StandardOutput.ReadLine();

                    if (diffLines == null && !line.StartsWith("diff"))
                    {
                        continue;
                    }

                    if (line.StartsWith("diff"))
                    {
                        addLastDiff();
                        diffLines = new List<string> { line };
                    }
                    else
                    {
                        diffLines.Add(line);
                    }
                }
            }

            addLastDiff();

            return diffs;
        }

        public static string[] GetFilePaths(string repoPath, string @object = null, string filter = null)
        {
            @object = @object ?? "HEAD";
            var results = Execute(string.Format("git ls-tree -r -z --full-name --name-only {0}", Q(@object)), repoPath);

            return results.Split('\0');
        }

        public static List<LogEntry> GetLogEntries(string repoPath, int count, int skip = 0, string @object = null, bool allRefs = false)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException("count");
            }

            if (skip < 0)
            {
                throw new ArgumentOutOfRangeException("skip");
            }

            @object = @object ?? "HEAD";
            var results = Execute(string.Format("log -n {0} --encoding=UTF-8 -z --date-order --format=\"format:commit %H%ntree %T%nparent %P%nauthor %an%nauthor mail %ae%nauthor date %aD%ncommitter %cn%ncommitter mail %ce%ncommitter date %cD%nsubject %s%n%b%x00\"{1} {2}", count + skip, allRefs ? " --all" : "", Q(@object)), repoPath, Encoding.UTF8);

            Func<string, LogEntry> parseResults = result =>
            {
                var commit = ParseResultLine("commit ", result, out result);
                var tree = ParseResultLine("tree ", result, out result);
                var parent = ParseResultLine("parent ", result, out result);
                var author = ParseResultLine("author ", result, out result);
                var authorEmail = ParseResultLine("author mail ", result, out result);
                var authorDate = ParseResultLine("author date ", result, out result);
                var committer = ParseResultLine("committer ", result, out result);
                var committerEmail = ParseResultLine("committer mail ", result, out result);
                var committerDate = ParseResultLine("committer date ", result, out result);
                var subject = ParseResultLine("subject ", result, out result);
                var body = result;

                return new LogEntry(commit, tree, parent, author, authorEmail, authorDate, committer, committerEmail, committerDate, subject, body);
            };

            return (from r in results.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries).Skip(skip)
                    select parseResults(r)).ToList();
        }

        public static RepoInfo GetRepoInfo(string repoPath)
        {
            var descrPath = Path.Combine(repoPath, "description");
            repoPath = Path.GetDirectoryName(descrPath);

            if (repoPath != null)
            {
                var repoName = Path.GetFileName(repoPath);

                string description = null;
                if (File.Exists(descrPath))
                {
                    description = File.ReadAllText(descrPath);
                }

                var isRepoBare = IsRepoDirectory(repoPath);

                var isRepo = isRepoBare || IsRepoDirectory(Path.Combine(repoPath, ".git"));

                var isArchived = IsArchived(repoPath);

                return new RepoInfo
                {
                    Name = repoName,
                    IsGitRepo = isRepo,
                    Description = description,
                    RepoPath = repoPath,
                    IsArchived = isArchived,
                    IsBare = isRepoBare,
                };
            }

            return new RepoInfo
            {
                Name = "",
                IsGitRepo = false,
                Description = "",
                RepoPath = repoPath,
                IsArchived = false,
                IsBare = false,
            };
        }

        public static TreeView GetTreeInfo(string repoPath, string tree, string path = null, bool recurse = false)
        {
            if (string.IsNullOrEmpty(tree))
            {
                throw new ArgumentNullException("tree");
            }

            if (!Regex.IsMatch(tree, "^[-.a-zA-Z0-9]+$"))
            {
                throw new ArgumentOutOfRangeException("tree", "tree mush be the id of a tree-ish object.");
            }

            path = path ?? string.Empty;
            string results;
            if (recurse)
            {
                results =
                    Execute(string.Format("ls-tree -l -r -z {0}:{1}", Q(tree), Q(path)), repoPath, Encoding.UTF8, trustErrorCode: true)
                    + '\0' +
                    Execute(string.Format("ls-tree -l -r -d -z {0}:{1}", Q(tree), Q(path)), repoPath, Encoding.UTF8, trustErrorCode: true);
            }
            else
            {
                results = Execute(string.Format("ls-tree -l -z {0}:{1}", Q(tree), Q(path)), repoPath, Encoding.UTF8, trustErrorCode: true);
            }

            Func<string, ObjectInfo> parseResults = result =>
            {
                var mode = ParseTreePart(result, "[ ]+", out result);
                var type = ParseTreePart(result, "[ ]+", out result);
                var hash = ParseTreePart(result, "[ ]+", out result);
                var size = ParseTreePart(result, "\\t+", out result);
                var name = result;

                return new ObjectInfo(
                    (ObjectType)Enum.Parse(typeof(ObjectType), type, ignoreCase: true),
                    hash,
                    size == "-" ? (int?)null : int.Parse(size),
                    name);
            };

            var objects = from r in results.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries)
                          select parseResults(r);
            IDictionary<string, SubmoduleInfo> submodules = null;
            try
            {
                var gitmodules = Execute(string.Format("show {0}:.gitmodules", Q(tree)), repoPath, Encoding.UTF8, trustErrorCode: true);
                submodules = GetSubmoduleUrls(gitmodules, path);
            }
            catch
            {
                // .gitmodules file does not exist
            }

            return new TreeView(tree, path, objects, submodules);
        }

        public static List<UserImpact> GetUserImpacts(string repoPath)
        {
            var renames = LoadRenames(repoPath);
            var ignores = LoadIgnores(repoPath);

            string impactData;
            using (var git = Start("log -z --format=%x01%H%x1e%ai%x1e%ae%x1e%an%x02 --numstat", repoPath, outputEncoding: Encoding.UTF8))
            {
                impactData = git.StandardOutput.ReadToEnd();
            }

            var individualImpacts = from imp in impactData.Split("\x01".ToArray(), StringSplitOptions.RemoveEmptyEntries)
                                    select ParseUserImpact(imp, renames, ignores);

            return individualImpacts.ToList();
        }

        /// <summary>
        /// Quotes and Escapes a command-line argument for Git and Bash.
        /// </summary>
        public static string Q(string argument)
        {
            var result = new StringBuilder(argument.Length + 10);
            result.Append("\"");
            for (int i = 0; i < argument.Length; i++)
            {
                var ch = argument[i];
                switch (ch)
                {
                    case '\\':
                    case '\"':
                        result.Append('\\');
                        result.Append(ch);
                        break;

                    default:
                        result.Append(ch);
                        break;
                }
            }

            result.Append("\"");
            return result.ToString();
        }

        public static Process Start(string command, string workingDir, bool redirectInput = false, bool redirectError = false, Encoding outputEncoding = null)
        {
            var git = WebConfigurationManager.AppSettings["GitCommand"];
            var startInfo = new ProcessStartInfo(git, command)
            {
                WorkingDirectory = workingDir,
                RedirectStandardInput = redirectInput,
                RedirectStandardOutput = true,
                RedirectStandardError = redirectError,
                StandardOutputEncoding = outputEncoding ?? DefaultEncoding,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            Process process = null, returnProcess = null;
            IDisposable trace = null, traceClosure = null;
            try
            {
                returnProcess = process = new Process();
                process.StartInfo = startInfo;
                process.EnableRaisingEvents = true;
                process.Exited += (s, e) =>
                {
                    if (traceClosure != null)
                    {
                        traceClosure.Dispose();
                    }
                };

                try
                {
                    var profiler = MiniProfiler.Current;
                    if (profiler != null)
                    {
                        traceClosure = trace = profiler.Step("git " + command);
                    }

                    process.Start();

                    trace = process = null;
                    return returnProcess;
                }
                finally
                {
                    if (trace != null)
                    {
                        trace.Dispose();
                    }
                }
            }
            finally
            {
                if (process != null)
                {
                    process.Dispose();
                }
            }
        }

        public static Process StartGetBlob(string repoPath, string tree, string path)
        {
            if (string.IsNullOrEmpty(tree))
            {
                throw new ArgumentNullException("tree");
            }

            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException("path");
            }

            if (!Regex.IsMatch(tree, "^[-a-zA-Z0-9]+$"))
            {
                throw new ArgumentOutOfRangeException("tree", "tree mush be the id of a tree-ish object.");
            }

            return Start(string.Format("show {0}:{1}", Q(tree), Q(path)), repoPath, redirectInput: false);
        }

        public static void ToggleArchived(string repoPath)
        {
            var file = Path.Combine(RepoInfoPath(repoPath), "archived");

            if (File.Exists(file))
            {
                File.Delete(file);
            }
            else
            {
                File.Create(file).Close();
            }
        }

        public static void UpdateServerInfo(string repoPath)
        {
            Execute("update-server-info", repoPath);
        }

        public static RefValidationResult ValidateRef(string repoPath, string refName)
        {
            if (refName == "HEAD")
            {
                return RefValidationResult.Valid;
            }

            if (string.IsNullOrWhiteSpace(refName))
            {
                return RefValidationResult.Invalid;
            }

            String results;
            int exitCode;

            using (var git = Start(string.Format("show-ref --heads --tags -- {0}", Q(refName)), repoPath))
            {
                results = git.StandardOutput.ReadToEnd();
                git.WaitForExit();
                exitCode = git.ExitCode;
            }

            if (exitCode != 0)
            {
                return RefValidationResult.Invalid;
            }

            if (results.TrimEnd('\n').IndexOf('\n') >= 0)
            {
                return RefValidationResult.Ambiguous;
            }

            return RefValidationResult.Valid;
        }

        private static IDictionary<string, SubmoduleInfo> GetSubmoduleUrls(string gitmodules, string path)
        {
            var submoduleList = new Dictionary<string, SubmoduleInfo>();
            if (gitmodules != null)
            {
                using (var reader = new StringReader(gitmodules))
                {
                    Regex headerRegex = new Regex(@"\[(.*)\s""(.*)""\]");
                    Regex itemsRegex = new Regex(@"\s*(.*)\s=\s(.*)");
                    string line = reader.ReadLine();
                    while (line != null)
                    {
                        Match match;
                        if ((match = headerRegex.Match(line)).Groups.Count == 3 && match.Groups[1].Value == "submodule")
                        {
                            try
                            {
                                var submodule = match.Groups[2].Value;
                                var items = new Dictionary<string, string>();
                                line = reader.ReadLine();
                                while (line != null && (match = itemsRegex.Match(line)).Groups.Count == 3)
                                {
                                    string key = match.Groups[1].Value;
                                    string value = match.Groups[2].Value;
                                    // parse url to make is useable
                                    if (key == "url")
                                    {
                                        // scp-like syntax is valid for git, but does not conform to uri standards
                                        // so we need to fix it up
                                        Regex scpLikeRegex = new Regex(@"\w*@.*:(\d*).*");
                                        if ((match = scpLikeRegex.Match(value)).Groups.Count > 1 && match.Groups[1].Length == 0)
                                        {
                                            value = value.Replace(":", "/");
                                        }
                                        // if the value does not start with a scheme, we assume that it is ssh, not that it really matters
                                        Regex schemeRegex = new Regex(@"\w*://.*");
                                        if (!schemeRegex.Match(value).Success)
                                        {
                                            value = "ssh" + Uri.SchemeDelimiter + value;
                                        }
                                        var uri = new Uri(value);
                                        // check to see if uri.Host is this server and fixup url
                                        if (uri.GetLeftPart(UriPartial.Authority) ==
                                            HttpContext.Current.Request.Url.GetLeftPart(UriPartial.Authority))
                                        {
                                            value = value.ToString().Replace("/git/", "/browse/");
                                        }
                                        // fixup github urls so that we can link there
                                        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                                        {
                                            value = Uri.UriSchemeHttps + Uri.SchemeDelimiter + uri.Host + uri.AbsolutePath;
                                            // drop the .git if present
                                            if (value.EndsWith(".git"))
                                            {
                                                value = value.Remove(value.Length - 4, 4);
                                            }
                                        }
                                        // TODO support other sites?
                                    }
                                    items.Add(key, value);
                                    line = reader.ReadLine();
                                }
                                string relativePath = items["path"];
                                if (!string.IsNullOrEmpty(path))
                                {
                                    if (!relativePath.StartsWith(path))
                                    {
                                        continue;
                                    }
                                    relativePath = relativePath.Substring(path.Length);
                                }
                                submoduleList.Add(relativePath, new SubmoduleInfo(relativePath, items["url"]));
                            }
                            catch
                            {
                                // ignore error
                            }
                        }
                        else
                        {
                            line = reader.ReadLine();
                        }
                    }
                }
            }
            return submoduleList;
        }

        private static bool IsArchived(string repoPath)
        {
            return File.Exists(Path.Combine(RepoInfoPath(repoPath), "archived"));
        }

        private static bool IsRepoDirectory(string repoPath)
        {
            // We use this method rather than 'git rev-parse --git-dir' or similar, because it takes
            // 0.0036 as much time.
            return (
                Directory.Exists(Path.Combine(repoPath, "refs")) &&
                Directory.Exists(Path.Combine(repoPath, "info")) &&
                Directory.Exists(Path.Combine(repoPath, "objects")) &&
                File.Exists(Path.Combine(repoPath, "HEAD")));
        }

        private static List<IgnoreEntry> LoadIgnores(string repoPath)
        {
            var ignoresFile = Path.Combine(RepoInfoPath(repoPath), "ignore");

            var ignores = new List<IgnoreEntry>();
            if (File.Exists(ignoresFile))
            {
                ignores.AddRange(IgnoreFileParser.Parse(File.ReadAllLines(ignoresFile)));
            }

            return ignores;
        }

        private static List<RenameEntry> LoadRenames(string repoPath)
        {
            var renames = new List<RenameEntry>();

            var parentRenames = Path.Combine(new DirectoryInfo(repoPath).Parent.FullName, "renames");
            var renamesFile = Path.Combine(RepoInfoPath(repoPath), "renames");

            Action<string> readRenames = (file) =>
            {
                if (File.Exists(file))
                {
                    renames.AddRange(RenameFileParser.Parse(File.ReadAllLines(file)));
                }
            };

            readRenames(parentRenames);
            readRenames(renamesFile);

            return renames;
        }

        private static string ParseResultLine(string prefix, string result, out string rest)
        {
            var parts = result.Split(new[] { '\n' }, 2);
            rest = parts[1];
            return parts[0].Substring(prefix.Length);
        }

        private static string ParseTreePart(string result, string delimiterPattern, out string rest)
        {
            var match = Regex.Match(result, delimiterPattern);

            if (!match.Success)
            {
                rest = result;
                return null;
            }
            else
            {
                rest = result.Substring(match.Index + match.Length);
                return result.Substring(0, match.Index);
            }
        }

        private static UserImpact ParseUserImpact(string impactData, IList<RenameEntry> renames, IList<IgnoreEntry> ignores)
        {
            var impactParts = impactData.Split("\x02".ToArray(), 2);
            var header = impactParts[0];
            var body = impactParts[1].TrimStart('\n');

            var headerParts = header.Split("\x1e".ToArray(), 4);
            var hash = headerParts[0];
            var date = headerParts[1];
            var email = headerParts[2];
            var name = headerParts[3];

            var author = Rename(new Author { Name = name, Email = email }, renames);

            var insertions = 0;
            var deletions = 0;

            var entries = body.Split("\0".ToArray(), StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in entries)
            {
                var entryParts = entry.Split("\t".ToArray(), 3);

                int ins, del;
                if (!int.TryParse(entryParts[0], out ins) || !int.TryParse(entryParts[1], out del))
                {
                    continue;
                }

                var path = entryParts[2];

                bool keepPath = ProcessIgnores(ignores, hash, path);

                if (keepPath)
                {
                    insertions += ins;
                    deletions += del;
                }
            }

            var commitDay = DateTimeOffset.Parse(date).ToUniversalTime().Date;

            return new UserImpact
            {
                Author = author.Name,
                Commits = 1,
                Insertions = insertions,
                Deletions = deletions,
                Impact = Math.Max(insertions, deletions),
                Date = commitDay,
            };
        }

        private static bool ProcessIgnores(IList<IgnoreEntry> ignores, string hash, string path)
        {
            for (int i = ignores.Count - 1; i >= 0; i--)
            {
                var ignore = ignores[i];
                if (hash.StartsWith(ignore.CommitHash) && ignore.IsMatch(path))
                {
                    return ignore.Negated;
                }
            }

            return true;
        }

        private static Author Rename(Author author, IList<RenameEntry> entries)
        {
            Func<RenameField, Author, string> getField = (f, a) =>
            {
                if (f == RenameField.Name)
                {
                    return a.Name;
                }

                if (f == RenameField.Email)
                {
                    return a.Email;
                }

                return null;
            };

            Action<RenameField, Author, string> setField = (f, a, v) =>
            {
                if (f == RenameField.Name)
                {
                    a.Name = v;
                }

                if (f == RenameField.Email)
                {
                    a.Email = v;
                }

                return;
            };

            author = new Author { Name = author.Name, Email = author.Email };

            foreach (var entry in entries)
            {
                switch (entry.RenameStyle)
                {
                    case RenameStyle.Exact:
                        if (getField(entry.SourceField, author) == entry.Match)
                        {
                            foreach (var dest in entry.Destinations)
                            {
                                setField(dest.Field, author, dest.Replacement);
                            }
                        }

                        break;

                    case RenameStyle.CaseInsensitive:
                        if (entry.Match.Equals(getField(entry.SourceField, author), StringComparison.CurrentCultureIgnoreCase))
                        {
                            foreach (var dest in entry.Destinations)
                            {
                                setField(dest.Field, author, dest.Replacement);
                            }
                        }

                        break;

                    case RenameStyle.Regex:
                        if (Regex.IsMatch(getField(entry.SourceField, author), entry.Match))
                        {
                            var newAuthor = new Author { Name = author.Name, Email = author.Email };
                            foreach (var dest in entry.Destinations)
                            {
                                setField(dest.Field, newAuthor, Regex.Replace(getField(entry.SourceField, author), entry.Match, dest.Replacement));
                            }

                            author = newAuthor;
                        }

                        break;
                }
            }

            return author;
        }

        private static string RepoInfoPath(string repoPath)
        {
            // Determine if the immediate path is a repository. If so, it is a bare repo
            var isBareRepo = IsRepoDirectory(repoPath);
            var path = repoPath;
            var isNonBareRepo = false;

            if (!isBareRepo)
            {
                // Since we didn't find a bare repo, look to see if this is a non-bare repo.
                string nonBareRepoPath = Path.Combine(repoPath, ".git");
                isNonBareRepo = IsRepoDirectory(nonBareRepoPath);

                if (isNonBareRepo)
                {
                    path = nonBareRepoPath;
                }
            }

            // Find our settings folder under info. If not there, create it.
            path = Path.Combine(path, "info", "webgit.net");

            if ((isBareRepo || isNonBareRepo) &&
                (!Directory.Exists(path)))
            {
                Directory.CreateDirectory(path);
            }

            return path;
        }

        private class Author
        {
            public string Email { get; set; }

            public string Name { get; set; }
        }
    }
}
