namespace MediaReconciler.Config;

public static class SampleConfig
{
    public const string Json = """
        {
          // Each source is one place photos live. Add as many as you like, including
          // several accounts of the same service. Names show up in the report.
          "sources": [
            {
              // iCloud Photos, downloaded by icloudpd (or the iCloud for Windows Photos folder).
              "name": "Wife iCloud",
              "type": "folder",
              "path": "D:\\Backups\\iCloud\\wife"
            },
            {
              // A Dropbox account, read through the API (metadata only, nothing is downloaded).
              // Get the token with: MediaReconciler dropbox-auth --app-key <key>
              "name": "Dropbox (John)",
              "type": "dropbox",
              "path": "/Camera Uploads",
              "appKey": "your-app-key",
              "refreshToken": "env:DROPBOX_JOHN"
            },
            {
              // Google Photos: the folder with the Takeout .zip parts from takeout.google.com.
              "name": "Google Photos (wife)",
              "type": "google-takeout",
              "path": "D:\\Takeout\\wife"
            },
            {
              // The phone itself over USB (Windows build only). Not a backup, so the report
              // shows what is only on the phone and what can safely be removed from it.
              "name": "Wife iPhone",
              "type": "iphone",
              "hash": false
            }
            // Also available:
            //   { "name": "...", "type": "manifest", "path": "inventory.csv" }  CSV from another program
            //   "isBackup": false                         on any source that doesn't count as a backup
            //   "exclude": ["Screenshots"]               folder names to skip
            //   "readOnlineOnlyFiles": true              hash cloud placeholder files (downloads them)
          ],

          // A photo counts as safe once it is in this many backup sources.
          "minBackupCopies": 2,

          // Match Dropbox's renamed "2024-05-01 12.34.56.jpg" copies by capture time.
          "timeOnlyMatching": true,

          "outputDirectory": "report"
        }
        """;
}
