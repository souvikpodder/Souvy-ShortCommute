using Bindito.Core;

namespace SouvyShortCommute.Overlay {

  /// <summary>
  /// Binds <see cref="CommuteOverlaySettings"/> in both the <c>MainMenu</c> and
  /// <c>Game</c> scopes so the settings panel finds it whether opened from the
  /// main-menu mod list or the in-game options. Kept separate from the
  /// <c>[Context("Game")]</c>-only <see cref="CommuteOverlayConfigurator"/> so the
  /// MainMenu binding is scoped to just the settings owner — widening the overlay
  /// configurator to MainMenu would drag its game-only singletons (which inject
  /// game services) into the main-menu container, where they'd fail to construct.
  /// </summary>
  [Context("MainMenu")]
  [Context("Game")]
  public class CommuteOverlaySettingsConfigurator : Configurator {

    /// <inheritdoc />
    protected override void Configure() {
      Bind<CommuteOverlaySettings>().AsSingleton();
    }

  }

}
