using UnityEngine;

namespace SouvyShortCommute.Overlay {

  /// <summary>
  /// Maps a home→workplace road distance (as stored on <see cref="CommuteCost"/>)
  /// to a discrete overlay colour band. Bands are deliberately wide
  /// (≥ ~2×<c>ClusterRadius</c>) so the block-resolution approximation baked into
  /// <see cref="CommuteCost"/> can never flip a house across a band edge.
  ///
  /// <para>Cutoffs are a starting ramp, to be tuned once a profiler/CSV capture
  /// shows the typical commute scale on a large map (see
  /// <c>docs/commute-overlay-plan.md</c>).</para>
  /// </summary>
  internal static class CommuteBands {

    #region Band colours

    /// <summary>≤ 20 road-tiles: a short, healthy commute.</summary>
    private static readonly Color Green = new(0.30f, 0.85f, 0.30f, 1f);

    /// <summary>20–40 road-tiles.</summary>
    private static readonly Color Yellow = new(0.95f, 0.90f, 0.25f, 1f);

    /// <summary>40–60 road-tiles.</summary>
    private static readonly Color Orange = new(0.98f, 0.60f, 0.15f, 1f);

    /// <summary>&gt; 60 road-tiles: a long commute the optimizer wants to fix.</summary>
    private static readonly Color Red = new(0.90f, 0.22f, 0.22f, 1f);

    /// <summary>Fallback for a line whose worker has no measured commute yet
    /// (<see cref="CommuteCost.NoData"/>) — drawn so the connection is still
    /// visible, but in a muted tone that reads as "unknown", not a band.</summary>
    private static readonly Color Neutral = new(0.70f, 0.70f, 0.75f, 1f);

    #endregion

    #region API

    /// <summary>
    /// The band colour for <paramref name="roadDistance"/>, or <c>null</c> when
    /// there is no data (<see cref="float.NaN"/> / unreachable). A null result
    /// means "do not colour this house" — the heatmap leaves no-data dwellings
    /// un-highlighted rather than painting them grey.
    /// </summary>
    public static Color? Resolve(float roadDistance) {
      if (float.IsNaN(roadDistance) || roadDistance >= float.MaxValue) {
        return null;
      }
      if (roadDistance <= 20f) {
        return Green;
      }
      if (roadDistance <= 40f) {
        return Yellow;
      }
      if (roadDistance <= 60f) {
        return Orange;
      }
      return Red;
    }

    /// <summary>
    /// As <see cref="Resolve"/>, but returns <see cref="Neutral"/> instead of
    /// <c>null</c> for no-data. Used by the line/path views, where the
    /// connection should always be drawn even if its distance is unknown.
    /// </summary>
    public static Color ResolveOrNeutral(float roadDistance) => Resolve(roadDistance) ?? Neutral;

    #endregion

  }

}
