using System.Collections.Generic;
using Timberborn.DwellingSystem;
using Timberborn.Navigation;
using Timberborn.WorkSystem;

namespace SouvyShortCommute {

  /// <summary>
  /// One dwelling's road distance to every relevant workplace, produced by a
  /// single exploration rooted at that dwelling. <see cref="Distances"/> is
  /// keyed by workplace, so "how far is this dwelling from job X?" is an O(1)
  /// lookup.
  /// </summary>
  internal sealed class DwellingRow {

    public readonly Dictionary<Workplace, float> Distances;

    public DwellingRow(Dictionary<Workplace, float> distances) => Distances = distances;

  }

  /// <summary>
  /// Caches <see cref="DwellingRow"/>s for the current rebalance pass under a
  /// hard per-tick measurement budget, with <b>block clustering</b>.
  ///
  /// <para><b>Dwelling-rooted.</b> A road-path query is a Dijkstra flow-field
  /// fill rooted at the start node, so rooting at the dwelling yields its
  /// distance to every workplace in one fill. Dwelling count is floored by
  /// dwelling capacity (it can't balloon like the workplace count), and swap
  /// evaluation becomes fill-free (its distances are lookups in two already-built
  /// rows).</para>
  ///
  /// <para><b>Clustering.</b> When we fill a dwelling's field, that field also
  /// holds its distance to every <em>other</em> dwelling — free lookups. Any
  /// dwelling within <c>clusterRadius</c> road-tiles is treated as part of the
  /// same block and handed the rep's row instead of getting its own fill. By the
  /// triangle inequality the error is bounded by the radius; with a ~10-tile
  /// "same block" radius that's well below typical commutes on a large map, so
  /// the cost is a small, bounded suboptimality on near-threshold moves while one
  /// fill covers an entire housing block. (Houses spread far apart — the layout
  /// this mod exists to support — simply don't cluster and keep their own fills.)</para>
  ///
  /// <para><b>Two lifetimes, composed.</b> Cached rows live for the whole pass
  /// (<see cref="Clear"/> at day start); the fill counter lives for one tick
  /// (<see cref="ResetTickCounter"/>). <see cref="TryGetRow"/> serves a hit for
  /// free, builds-and-clusters a miss if under budget, and returns <c>null</c>
  /// when a fresh fill is needed but the tick's budget is spent — telling the
  /// caller to defer and resume later, with prior fills (and their clusters)
  /// still cached.</para>
  /// </summary>
  internal sealed class DwellingRowCache {

    private readonly Dictionary<Dwelling, DwellingRow> _rows = new();
    private readonly int _maxFillsPerTick;
    private readonly float _clusterRadius;

    /// <summary>Active dwellings this tick — the pool clustering draws members from.</summary>
    private IReadOnlyList<Dwelling> _clusterDwellings = System.Array.Empty<Dwelling>();

    public DwellingRowCache(int maxFillsPerTick, float clusterRadius) {
      _maxFillsPerTick = maxFillsPerTick;
      _clusterRadius = clusterRadius;
    }

    /// <summary>Fresh fills performed since the last <see cref="ResetTickCounter"/>.
    /// One fill covers a whole cluster, so this counts clusters, not dwellings.</summary>
    public int FillsThisTick { get; private set; }

    /// <summary>Reset the per-tick fill counter (call at the start of each tick).</summary>
    public void ResetTickCounter() => FillsThisTick = 0;

    /// <summary>Drop all cached rows (call at the start of each rebalance pass).</summary>
    public void Clear() => _rows.Clear();

    /// <summary>Provide the active-dwelling list used to find cluster members (call each tick).</summary>
    public void SetClusterDwellings(IReadOnlyList<Dwelling> dwellings) => _clusterDwellings = dwellings;

    /// <summary>
    /// Return the dwelling's row. A cache hit (including a clustered member) is
    /// free; a miss builds the row (one fill) and clusters its block, if the
    /// per-tick budget allows, otherwise returns <c>null</c> so the caller defers.
    /// </summary>
    public DwellingRow? TryGetRow(Dwelling dwelling, IReadOnlyList<Workplace> workplaces) {
      if (_rows.TryGetValue(dwelling, out var row)) {
        return row;
      }
      if (FillsThisTick >= _maxFillsPerTick) {
        return null; // budget spent this tick — caller defers; rows already built stay cached
      }
      row = BuildAndCluster(dwelling, workplaces);
      FillsThisTick++;
      return row;
    }

    private DwellingRow BuildAndCluster(Dwelling rep, IReadOnlyList<Workplace> workplaces) {
      var row = Build(rep, workplaces); // the one fill, rooted at rep
      _rows[rep] = row;

      // The rep's flow field is now warm, so rep -> each other dwelling is a cheap
      // lookup. Dwellings within the block radius share the rep's row (their true
      // distances differ by at most that radius), so this one fill covers the block.
      if (_clusterRadius > 0f) {
        var repAccessible = rep.GetEnabledComponent<Accessible>();
        if (repAccessible != null) {
          for (var i = 0; i < _clusterDwellings.Count; i++) {
            var other = _clusterDwellings[i];
            if (other == rep || _rows.ContainsKey(other)) {
              continue;
            }
            var otherAccessible = other.GetEnabledComponent<Accessible>();
            if (otherAccessible != null
                && repAccessible.FindRoadPath(otherAccessible, out var distance)
                && distance <= _clusterRadius) {
              _rows[other] = row; // clustered: reuse the rep's row (approximate within the radius)
            }
          }
        }
      }
      return row;
    }

    private static DwellingRow Build(Dwelling dwelling, IReadOnlyList<Workplace> workplaces) {
      var distances = new Dictionary<Workplace, float>(workplaces.Count);
      var dwellingAccessible = dwelling.GetEnabledComponent<Accessible>();
      if (dwellingAccessible != null) {
        for (var i = 0; i < workplaces.Count; i++) {
          var workplace = workplaces[i];
          var workplaceAccessible = workplace.GetEnabledComponent<Accessible>();
          if (workplaceAccessible == null) {
            continue;
          }
          // Root at the dwelling: all workplaces share this one flow-field fill.
          if (dwellingAccessible.FindRoadPath(workplaceAccessible, out var distance)) {
            distances[workplace] = distance;
          } else if (workplaceAccessible == dwellingAccessible) {
            distances[workplace] = 0f;
          }
        }
      }
      return new DwellingRow(distances);
    }

  }

}
