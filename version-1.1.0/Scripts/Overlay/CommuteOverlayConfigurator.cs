using Bindito.Core;

namespace SouvyShortCommute.Overlay {

  /// <summary>
  /// Binds the commute overlay singletons. They implement Timberborn lifecycle
  /// interfaces (<c>ILoadableSingleton</c> / <c>IUpdatableSingleton</c>) which
  /// Bindito honours for bound singletons, so binding here is all the wiring the
  /// overlay needs — no template decorators (the overlay reads the
  /// <see cref="CommuteCost"/> the main mod already stamps). The
  /// <see cref="CommuteOverlayPatcher"/> applies the overlay's two surgical
  /// Harmony suppressions on load.
  /// </summary>
  [Context("Game")]
  public class CommuteOverlayConfigurator : Configurator {

    /// <inheritdoc />
    protected override void Configure() {
      Bind<CommuteOverlayToggle>().AsSingleton();
      Bind<CommuteLineDrawer>().AsSingleton();
      Bind<CommuteOverlayRenderer>().AsSingleton();
      Bind<CommuteOverlayPatcher>().AsSingleton();
    }

  }

}
