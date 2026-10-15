using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Shared.Logging;
using VRage.Game;

namespace ServerPlugin;

// Asks the Grid Backups plugin, when the server runs it, for a backup of each grid
// group before it is concealed. Concealment has no reference to Grid Backups, so
// it calls its public Plugin.BackupBeforeConcealment entry point by reflection.
public sealed class GridBackups
{
    private readonly Func<List<MyObjectBuilder_CubeGrid>, long, bool> backupBeforeConcealment;
    private readonly IPluginLogger log;

    private GridBackups(
        Func<List<MyObjectBuilder_CubeGrid>, long, bool> backupBeforeConcealment,
        IPluginLogger log
    )
    {
        this.backupBeforeConcealment = backupBeforeConcealment;
        this.log = log;
    }

    // Both plugins name their entry class ServerPlugin.Plugin, so Grid Backups is
    // the one with the entry point. Null if the server doesn't run Grid Backups.
    public static GridBackups Find(IPluginLogger log)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type;
            try
            {
                type = assembly.GetType("ServerPlugin.Plugin", false);
            }
            catch (Exception)
            {
                // Some other plugin's assembly that can't load its types
                continue;
            }

            var method = type?.GetMethod(
                "BackupBeforeConcealment",
                new[] { typeof(List<MyObjectBuilder_CubeGrid>), typeof(long) }
            );
            var instance = type
                ?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);

            if (method == null || method.ReturnType != typeof(bool) || instance == null)
                continue;

            var call =
                (Func<List<MyObjectBuilder_CubeGrid>, long, bool>)
                    method.CreateDelegate(
                        typeof(Func<List<MyObjectBuilder_CubeGrid>, long, bool>),
                        instance
                    );
            return new GridBackups(call, log);
        }

        return null;
    }

    // Takes the object builders on the calling thread, before anything changes the
    // grids, and lets Grid Backups write the file on a worker thread, as its own
    // scans do. Grid Backups owns the policy: whether the feature is on, and the
    // minimum time between two backups of the same grid.
    public void BackUp(ConcealGroup group)
    {
        try
        {
            var owner = group
                .Grids.OrderByDescending(g => g.BlocksCount)
                .First()
                .BigOwners.FirstOrDefault();
            var builders = group
                .Grids.Select(g => g.GetObjectBuilder())
                .OfType<MyObjectBuilder_CubeGrid>()
                .ToList();
            Task.Run(() =>
            {
                try
                {
                    backupBeforeConcealment(builders, owner);
                }
                catch (Exception ex)
                {
                    log.Error(ex, "Grid Backups failed to back up {0}", group.GridNames);
                }
            });
        }
        catch (Exception ex)
        {
            log.Error(
                ex,
                "Failed to take the object builders of {0} for Grid Backups",
                group.GridNames
            );
        }
    }
}
