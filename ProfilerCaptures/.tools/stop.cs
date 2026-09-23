string root = System.IO.Path.GetFullPath((string)options["outputDirectory"]);
string run = (string)options["runName"];
if (string.IsNullOrWhiteSpace(run) || run.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
    throw new ArgumentException("runName must be a filename, without a directory.");
string prefix = System.IO.Path.Combine(root, run);
if (!System.IO.File.Exists(prefix + "-started.json")) throw new InvalidOperationException("No recording exists for this run.");
System.IO.File.WriteAllText(prefix + "-stop.txt", DateTime.UtcNow.ToString("O"));
return new { stopRequested = true, status = prefix + "-status.json", error = prefix + "-error.txt" };
