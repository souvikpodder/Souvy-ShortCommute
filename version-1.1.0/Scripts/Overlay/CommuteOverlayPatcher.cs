using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Timberborn.Beavers;
using Timberborn.DwellingSystem;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace SouvyShortCommute.Overlay {

  /// <summary>
  /// Applies the overlay's surgical Harmony patches and DwellerHomeAssigner safety guards once at game load.
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
        applied += PatchMethod(harmony,
            "Timberborn.DistanceHeatmap.DistanceHeatmapShower", "ShowHeatmap",
            SkipWhenOverlayActiveMethod) ? 1 : 0;
        applied += PatchMethod(harmony,
            "Timberborn.MechanicalSystemHighlighting.MechanicalGraphHighlightService",
            "HighlightSelectedNode", SkipWhenOverlayActiveMethod) ? 1 : 0;
        applied += PatchMethod(harmony,
            "Timberborn.BuildingsNavigation.DistrictPathNavRangeDrawer", "LateUpdate",
            SkipPathRangeMethod) ? 1 : 0;
        applied += PatchMethod(harmony,
            "Timberborn.DwellingSystem.DwellerHomeAssigner", "AssignDweller",
            AssignDwellerPrefixMethod) ? 1 : 0;
        applied += PatchMethod(harmony,
            "Timberborn.DwellingSystem.DwellerHomeAssigner", "AddDweller",
            AddDwellerPrefixMethod) ? 1 : 0;
        Debug.Log($"[ShortCommute] Harmony patches: applied {applied}/5 prefixes.");
      } catch (System.Exception ex) {
        // Last-resort net (e.g. Harmony itself unavailable). Loud but non-fatal.
        Debug.LogError($"[ShortCommute] Harmony patches failed to apply "
                       + $"(is 0Harmony.dll loaded?): {ex}");
      }
    }

    #endregion

    #region Patching

    /// <summary>Prefix the named method with the given prefix handler.</summary>
    private static bool PatchMethod(Harmony harmony, string typeName, string methodName,
        MethodInfo prefix) {
      try {
        var type = AccessTools.TypeByName(typeName);
        if (type == null) {
          Debug.LogError($"[ShortCommute] Patch target: type '{typeName}' not found.");
          return false;
        }
        var method = AccessTools.Method(type, methodName);
        if (method == null) {
          Debug.LogError($"[ShortCommute] Patch target: method '{typeName}.{methodName}' not found.");
          return false;
        }
        harmony.Patch(method, prefix: new HarmonyMethod(prefix));
        return true;
      } catch (System.Exception ex) {
        Debug.LogError($"[ShortCommute] Failed to patch '{typeName}.{methodName}' — {ex.Message}.");
        return false;
      }
    }

    private static readonly MethodInfo SkipWhenOverlayActiveMethod =
        AccessTools.Method(typeof(CommuteOverlayPatcher), nameof(SkipWhenOverlayActive));

    private static readonly MethodInfo SkipPathRangeMethod =
        AccessTools.Method(typeof(CommuteOverlayPatcher), nameof(SkipPathRangeWhenEnabled));

    private static readonly MethodInfo AssignDwellerPrefixMethod =
        AccessTools.Method(typeof(CommuteOverlayPatcher), nameof(AssignDwellerSafetyPrefix));

    private static readonly MethodInfo AddDwellerPrefixMethod =
        AccessTools.Method(typeof(CommuteOverlayPatcher), nameof(AddDwellerSafetyPrefix));

    /// <summary>Harmony prefix: returning <c>false</c> skips the original. We skip
    /// (suppress the vanilla highlight) exactly when the overlay is active.</summary>
    private static bool SkipWhenOverlayActive() => !CommuteOverlaySuppression.Active;

    /// <summary>Harmony prefix for the path-range mesh: skip it only when the
    /// overlay is active <em>and</em> the player opted into hiding it.</summary>
    private static bool SkipPathRangeWhenEnabled() =>
        !(CommuteOverlaySuppression.Active && CommuteOverlaySuppression.HidePathRange);

    /// <summary>
    /// Harmony prefix on DwellerHomeAssigner.AssignDweller:
    /// Sanitizes the primary and secondary beaver collections so that destroyed, despawned,
    /// or uninitialized beaver instances are filtered out before ComponentCache is queried.
    /// </summary>
    private static void AssignDwellerSafetyPrefix(ref IEnumerable<Beaver> primaryBeavers, ref IEnumerable<Beaver> secondaryBeavers) {
      if (primaryBeavers != null) {
        primaryBeavers = SafeFilterBeavers(primaryBeavers);
      }
      if (secondaryBeavers != null) {
        secondaryBeavers = SafeFilterBeavers(secondaryBeavers);
      }
    }

    /// <summary>
    /// Harmony prefix on DwellerHomeAssigner.AddDweller:
    /// Validates the dwelling component before assigning dwellers.
    /// </summary>
    private static bool AddDwellerSafetyPrefix(Timberborn.BaseComponentSystem.BaseComponent dwelling) {
      return dwelling && dwelling.GameObject != null && dwelling.Enabled;
    }

    private static IEnumerable<Beaver> SafeFilterBeavers(IEnumerable<Beaver> beavers) {
      foreach (var beaver in beavers) {
        if (beaver && beaver.GameObject != null && beaver.Enabled && beaver.HasComponent<Dweller>()) {
          yield return beaver;
        }
      }
    }

    #endregion

  }

}
