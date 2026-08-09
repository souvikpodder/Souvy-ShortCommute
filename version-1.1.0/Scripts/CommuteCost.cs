using Timberborn.BaseComponentSystem;

namespace SouvyShortCommute {

  /// <summary>
  /// Display-only record of a beaver's current work-path cost: the road distance
  /// from its home to its workplace, as last measured by
  /// <see cref="CommuteOptimizer"/> during a rebalance pass. Attached to every
  /// entity that has a <see cref="Timberborn.WorkSystem.Worker"/> (via an
  /// <c>AddDecorator&lt;Worker, CommuteCost&gt;</c> in
  /// <see cref="ShortCommuteConfigurator"/>). The future commute overlay reads it;
  /// the optimizer's own move/swap logic never does.
  ///
  /// <para><b>Block-resolution, by design.</b> With dwelling clustering enabled
  /// (<see cref="DwellingRowCache"/>), the value is the cluster representative's
  /// distance, so same-block neighbours report the same cost — off by at most the
  /// cluster radius. The overlay is meant to present this in coarse colour bands
  /// (band width well above the cluster radius), where the approximation is
  /// invisible. Do <b>not</b> surface it as an exact per-beaver number without an
  /// on-select recompute, or same-block houses will visibly show identical values
  /// that aren't truly identical.</para>
  ///
  /// <para><b>Staleness, by design.</b> The value is "as of the last rebalance".
  /// It is deliberately not reset at pass start, so a not-yet-processed beaver
  /// keeps showing its previous reading rather than flickering to "no data" each
  /// day. This matches the distance-staleness tolerance the optimizer already
  /// accepts; the overlay legend should read as a last-rebalance snapshot.</para>
  ///
  /// <para>Not persisted — it is recomputed every pass, so saves carry nothing new
  /// and the mod stays cleanly removable.</para>
  /// </summary>
  public class CommuteCost : BaseComponent {

    #region State

    /// <summary>Sentinel for "no commute measured" — no home, an unreachable
    /// home→job pair, or a beaver the optimizer has not processed yet.</summary>
    public const float NoData = float.NaN;

    /// <summary>Road distance from the beaver's home to its workplace, or
    /// <see cref="NoData"/> when no value has been measured.</summary>
    public float RoadDistance { get; private set; } = NoData;

    /// <summary>True when <see cref="RoadDistance"/> holds a measured value.</summary>
    public bool HasData => !float.IsNaN(RoadDistance);

    #endregion

    #region Mutation

    /// <summary>Record a freshly measured home-to-workplace road distance.</summary>
    public void Set(float roadDistance) => RoadDistance = roadDistance;

    /// <summary>Mark the cost unknown (no home, unreachable, or unemployed).</summary>
    public void Clear() => RoadDistance = NoData;

    #endregion

  }

}
