using MediaReconciler.Config;
using MediaReconciler.Hashing;
using MediaReconciler.Matching;
using MediaReconciler.Model;
using MediaReconciler.Reporting;
using MediaReconciler.Sources;

namespace MediaReconciler;

/// <summary>Scans every source, matches the items and builds the analysis.</summary>
public sealed class Reconciler(ReconcileConfig config, ScanCache cache, HttpClient http, Action<string> log)
{
    public async Task<Analysis> RunAsync(CancellationToken ct)
    {
        var all = new List<MediaItem>();
        var sources = new List<SourceInfo>();

        for (var index = 0; index < config.Sources.Count; index++)
        {
            var sourceConfig = config.Sources[index];
            var context = new ScanContext(index, sourceConfig, cache, log);
            context.Log($"scanning ({sourceConfig.Type})...");

            var started = DateTime.UtcNow;
            var items = await MediaSourceFactory.Create(sourceConfig, http).ScanAsync(context, ct);
            context.Log($"{items.Count:N0} photos/videos in {(DateTime.UtcNow - started).TotalSeconds:0}s");

            all.AddRange(items);
            sources.Add(new SourceInfo(index, sourceConfig.Name, sourceConfig.Type, sourceConfig.CountsAsBackup) { Notes = context.Notes });
        }

        log($"matching {all.Count:N0} items...");
        var groups = Matcher.Match(all, config.TimeOnlyMatching);
        return Analysis.Compute(sources, groups, config.MinBackupCopies);
    }
}
