using System.Reflection;
using ModSettings.Core;
using Timberborn.Modding;
using Timberborn.SettingsSystem;

namespace SouvyShortCommute.Overlay {

  /// <summary>
  /// In-game mod settings for the commute overlay.
  /// </summary>
  public class CommuteOverlaySettings : ModSettingsOwner {

    public ModSetting<bool> HidePathRangeOverlay { get; } =
        new(true,
            ModSettingDescriptor
                .Create("Hide path-range overlay during commute analysis")
                .SetTooltip("While the commute analysis overlay is on, suppress the vanilla "
                            + "road/nav range mesh that path-range buildings (dwellings, "
                            + "ziplines, tubeways) draw when selected."));

    public override ModSettingsContext ChangeableOn => ModSettingsContext.All;

    protected override string ModId => "Souvy.ShortCommute";

    public CommuteOverlaySettings(ISettings settings,
        ModSettingsOwnerRegistry modSettingsOwnerRegistry, ModRepository modRepository)
        : base(settings, modSettingsOwnerRegistry, modRepository) {
      CleanupDuplicateRegistrations(modSettingsOwnerRegistry);
    }

    private void CleanupDuplicateRegistrations(ModSettingsOwnerRegistry modSettingsOwnerRegistry) {
      var field = typeof(ModSettingsOwnerRegistry).GetField("_modSettingOwners", BindingFlags.NonPublic | BindingFlags.Instance);
      if (field != null) {
        var dict = field.GetValue(modSettingsOwnerRegistry) as System.Collections.IDictionary;
        if (dict != null) {
          foreach (System.Collections.IList list in dict.Values) {
            if (list != null && list.Contains(this) && list.Count > 1) {
              list.Remove(this);
              break;
            }
          }
        }
      }
    }

  }

}
