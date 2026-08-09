using System.Reflection;
using HarmonyLib;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace SouvyShortCommute.Overlay {

  /// <summary>
  /// Applies the overlay's surgical Harmony patches once at game load. Each is a
  /// prefix that skips a single vanilla method while the commute overlay is on:
  /// <list type="bullet">
  ///   <item><c>DistanceHeatmapShower.ShowHeatmap</c> — the vanilla building
  ///   distance heatmap shown on selecting any building with a path range. It
  ///   paints the same <c>Secondary</c> highlight layer the overlay uses, so it
  ///   would overwrite the overlay's commute colours. (Gated on
  ///   <see cref="CommuteOverlaySuppression.Active"/>.)</item>
  ///   <item><c>MechanicalGraphHighlightService.HighlightSelectedNode</c> — the
  ///   power-network highlight shown on selecting a powered building. (Gated on
  ///   <c>Active</c>.)</item>
  ///   <item><c>DistrictPathNavRangeDrawer.LateUpdate</c> — the vanilla road/nav
  ///   range mesh path-range buildings draw on selection. It rebuilds its whole
  ///   per-tile mesh as the selected/hovered target changes (a measured ~17 ms+
  ///   per frame on large path networks). Heavy enough that it's gated on
  ///   <c>Active</c> <em>and</em> the player opt-in
  ///   <see cref="CommuteOverlaySuppression.HidePathRange"/> rather than suppressed
  ///   unconditionally.</item>
  /// </list>
  ///
  /// <para>These are the <em>only</em> Harmony patches in the mod — narrow,
  /// reversible (gated by a flag, not a behaviour rewrite), and inert while the
  /// overlay is off (the prefix returns <c>true</c>, so vanilla runs untouched).
  /// All targets are <c>internal</c> in other assemblies, so they're resolved by
  /// name via <see cref="AccessTools"/> and patched manually rather than by
  /// attribute.</para>
  /// </summary>
  public sealed class CommuteOverlayPatcher : ILoadableSingleton {

    #region Constants

    private const string HarmonyId = "SouvyShortCommute.Overlay";

    #endregion

    #region State

    /// <summary>Guard so a save reload (which re-runs Game-context Load) doesn't
    /// re-apply the patches.</summary>
    private static bool _patched;

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public void Load() {
      if (_patched) {
        return;
      }
      // Set before patching: a save reload re-runs this Load, and we never want to
      // re-apply (and stack) patches that already succeeded. Each patch below is
      // independently guarded, so a partial failure still leaves us "done".
      _patched = true;
      try {
        var harmony = new Harmony(HarmonyId);
        var applied = 0;
        applied += PatchSuppression(harmony,
            "Timberborn.DistanceHeatmap.DistanceHeatmapShower", "ShowHeatmap",
            SkipWhenOverlayActiveMethod) ? 1 : 0;
        applied += PatchSuppression(harmony,
            "Timberborn.MechanicalSystemHighlighting.MechanicalGraphHighlightService",
            "HighlightSelectedNode", SkipWhenOverlayActiveMethod) ? 1 : 0;
        applied += PatchSuppression(harmony,
            "Timberborn.BuildingsNavigation.DistrictPathNavRangeDrawer", "LateUpdate",
            SkipPathRangeMethod) ? 1 : 0;
        Debug.Log($"[ShortCommute] Overlay suppression: applied {applied}/3 Harmony prefixes.");
      } catch (System.Exception ex) {
        // Last-resort net (e.g. Harmony itself unavailable). Loud but non-fatal:
        // the overlay still works, just without suppression — no game state is touched.
        Debug.LogError($"[ShortCommute] Overlay suppression patches failed to apply "
                       + $"(is 0Harmony.dll loaded?): {ex}");
      }
    }

    #endregion

    #region Patching

    /// <summary>Prefix the named (instance, parameterless) method with the
    /// suppression gate. Fully self-contained and non-throwing: a missing target
    /// (game update) or a patch that won't apply is logged and skipped, so the
    /// other suppressions — and the overlay itself — keep working. Returns whether
    /// this one went on.</summary>
    private static bool PatchSuppression(Harmony harmony, string typeName, string methodName,
        MethodInfo prefix) {
      try {
        var type = AccessTools.TypeByName(typeName);
        if (type == null) {
          Debug.LogError($"[ShortCommute] Overlay suppression: type '{typeName}' not found — "
                         + "vanilla behaviour will not be suppressed under the overlay.");
          return false;
        }
        var method = AccessTools.Method(type, methodName);
        if (method == null) {
          Debug.LogError($"[ShortCommute] Overlay suppression: method '{typeName}.{methodName}' "
                         + "not found — vanilla behaviour will not be suppressed under the overlay.");
          return false;
        }
        harmony.Patch(method, prefix: new HarmonyMethod(prefix));
        return true;
      } catch (System.Exception ex) {
        // Non-fatal: this one suppression is off, the rest of the mod is unaffected.
        Debug.LogError($"[ShortCommute] Overlay suppression: failed to patch "
                       + $"'{typeName}.{methodName}' — {ex.Message}. Continuing without it.");
        return false;
      }
    }

    private static readonly MethodInfo SkipWhenOverlayActiveMethod =
        AccessTools.Method(typeof(CommuteOverlayPatcher), nameof(SkipWhenOverlayActive));

    private static readonly MethodInfo SkipPathRangeMethod =
        AccessTools.Method(typeof(CommuteOverlayPatcher), nameof(SkipPathRangeWhenEnabled));

    /// <summary>Harmony prefix: returning <c>false</c> skips the original. We skip
    /// (suppress the vanilla highlight) exactly when the overlay is active.</summary>
    private static bool SkipWhenOverlayActive() => !CommuteOverlaySuppression.Active;

    /// <summary>Harmony prefix for the path-range mesh: skip it only when the
    /// overlay is active <em>and</em> the player opted into hiding it.</summary>
    private static bool SkipPathRangeWhenEnabled() =>
        !(CommuteOverlaySuppression.Active && CommuteOverlaySuppression.HidePathRange);

    #endregion

  }

}
