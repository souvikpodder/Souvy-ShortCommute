using System.Collections.Generic;
using HarmonyLib;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.DwellingSystem;
using Timberborn.EntitySystem;
using Timberborn.Navigation;
using Timberborn.SelectionSystem;
using Timberborn.SingletonSystem;
using Timberborn.WorkSystem;
using UnityEngine;

namespace SouvyShortCommute.Overlay {

  /// <summary>
  /// Drives the commute overlay while <see cref="CommuteOverlayToggle"/> is on.
  /// Context-sensitive by selection:
  /// <list type="bullet">
  ///   <item><b>Nothing selected</b> — a whole-map heatmap: every dwelling is
  ///   secondary-highlighted with the band of its <em>worst</em> occupant's
  ///   commute (<see cref="CommuteBands"/>).</item>
  ///   <item><b>House selected</b> — straight coloured lines to each workplace its
  ///   occupants work at, with those workplaces light-blue-highlighted.</item>
  ///   <item><b>Workplace selected</b> — straight coloured lines to the homes its
  ///   workers live in, with those homes light-blue-highlighted.</item>
  ///   <item><b>Beaver selected</b> — its home and workplace light-blue-highlighted
  ///   and connected by the walked path (a straight line if the path can't be
  ///   built); one pathfind, off the optimizer's tick path.</item>
  /// </list>
  ///
  /// <para>Link highlights reuse the vanilla power-network highlight colour
  /// (resolved from its spec) on the same <c>Secondary</c> layer; while anything is
  /// selected the whole-map heatmap is cleared, so the two never overlap.</para>
  ///
  /// <para>The heatmap uses an isolated <see cref="Highlighter"/> instance (bound
  /// transient, so ours is our own) writing the <c>Secondary</c> layer — the same
  /// approach the vanilla power-network overlay uses — so it rides alongside the
  /// game's selection highlight (which owns the <c>Primary</c> layer) rather than
  /// fighting it. Heatmap colours are only re-applied when a house's band changes;
  /// the per-frame cost is otherwise a band recompute and no Highlighter churn.</para>
  /// </summary>
  public sealed class CommuteOverlayRenderer : ILoadableSingleton, IUpdatableSingleton {

    #region Constants

    /// <summary>Vertical lift applied to line endpoints/corners so lines float
    /// just above the ground instead of z-fighting terrain.</summary>
    private const float LineHeight = 0.5f;

    /// <summary>Light blue used to highlight the entities linked to the current
    /// selection (a house's workplaces, a workplace's homes, a beaver's pair).
    /// Resolved from the vanilla power-network spec at runtime to match it
    /// exactly; this is the fallback if that lookup fails.</summary>
    private static readonly Color FallbackLinkColor = new(0.40f, 0.78f, 1f, 1f);

    private const string LinkColorSpecType =
        "Timberborn.MechanicalSystemHighlighting.MechanicalNodeHighlighterSpec";

    #endregion

    #region Dependencies

    private readonly CommuteOverlayToggle _toggle;
    private readonly EntityComponentRegistry _entityRegistry;
    private readonly EntitySelectionService _selectionService;
    private readonly CommuteLineDrawer _lines;
    private readonly Highlighter _highlighter;
    private readonly ISpecService _specService;
    private readonly CommuteOverlaySettings _settings;
    private readonly EventBus _eventBus;

    #endregion

    #region State

    /// <summary>True while the overlay is actively drawing (toggle on).</summary>
    private bool _active;

    /// <summary>True while an entity is selected. The whole-map heatmap is the
    /// <em>nothing-selected</em> state; once something is selected we hide it and
    /// draw only that selection's lines/path, so the selection reads clearly.</summary>
    private bool _hasSelection;

    /// <summary>Dwellings we have secondary-highlighted, and the band colour each
    /// currently shows — so we only re-highlight on a band change.</summary>
    private readonly Dictionary<Dwelling, Color> _highlighted = new();

    /// <summary>Entities we have light-blue-highlighted for the current selection
    /// (its linked workplaces/homes/endpoints), so we can clear them when the
    /// selection changes. Disjoint from <see cref="_highlighted"/> — the heatmap is
    /// cleared while anything is selected.</summary>
    private readonly HashSet<BaseComponent> _selectionHighlights = new();

    /// <summary>Cached link-highlight colour (resolved lazily on first selection).</summary>
    private Color? _linkColor;

    /// <summary>Reusable buffers for the beaver path draw (avoid per-select alloc).</summary>
    private readonly List<PathCorner> _cornerBuffer = new();
    private readonly List<Vector3> _pathBuffer = new();

    #endregion

    public CommuteOverlayRenderer(CommuteOverlayToggle toggle, EntityComponentRegistry entityRegistry,
        EntitySelectionService selectionService, CommuteLineDrawer lines, Highlighter highlighter,
        ISpecService specService, CommuteOverlaySettings settings, EventBus eventBus) {
      _toggle = toggle;
      _entityRegistry = entityRegistry;
      _selectionService = selectionService;
      _lines = lines;
      _highlighter = highlighter;
      _specService = specService;
      _settings = settings;
      _eventBus = eventBus;
    }

    #region Lifecycle

    /// <inheritdoc />
    public void Load() => _eventBus.Register(this);

    /// <inheritdoc />
    public void UpdateSingleton() {
      if (_toggle.Enabled) {
        CommuteOverlaySuppression.HidePathRange = _settings.HidePathRangeOverlay.Value;
        if (!_active) {
          _active = true;
          CommuteOverlaySuppression.Active = true;
          var selected = _selectionService.SelectedObject;
          _hasSelection = selected != null;
          if (selected != null) {
            DrawForSelection(selected);
          }
        }
        if (!_hasSelection) {
          RefreshHeatmap();
        }
      } else if (_active) {
        _active = false;
        _hasSelection = false;
        CommuteOverlaySuppression.Active = false;
        ClearAll();
      }
    }

    #endregion

    #region Selection handling

    [OnEvent]
    public void OnObjectSelected(SelectableObjectSelectedEvent selectedEvent) {
      if (!_active) {
        return;
      }
      _hasSelection = true;
      ClearHeatmap(); // drop the whole-map heatmap so only this selection shows
      DrawForSelection(selectedEvent.SelectableObject);
    }

    [OnEvent]
    public void OnObjectUnselected(SelectableObjectUnselectedEvent unselectedEvent) {
      _hasSelection = false;
      ClearSelection();
      // The heatmap is restored on the next UpdateSingleton (nothing selected).
    }

    /// <summary>Dispatch by what was selected — beaver, house, or workplace. Each
    /// case draws connector lines and light-blue-highlights the linked entities.</summary>
    private void DrawForSelection(SelectableObject selectable) {
      ClearSelection();
      if (selectable == null) {
        return;
      }
      var worker = selectable.GetComponent<Worker>() ?? selectable.GameObject.GetComponentInParent<Worker>() ?? selectable.GameObject.GetComponentInChildren<Worker>();
      var dwelling = selectable.GetComponent<Dwelling>() ?? selectable.GameObject.GetComponentInParent<Dwelling>() ?? selectable.GameObject.GetComponentInChildren<Dwelling>();
      var workplace = selectable.GetComponent<Workplace>() ?? selectable.GameObject.GetComponentInParent<Workplace>() ?? selectable.GameObject.GetComponentInChildren<Workplace>();

      if (worker != null) {
        DrawBeaverPath(worker);
      } else if (dwelling != null) {
        DrawHouseLines(dwelling);
      } else if (workplace != null) {
        DrawWorkplaceLines(workplace);
      }
    }

    #endregion

    #region Heatmap

    private void RefreshHeatmap() {
      foreach (var dwelling in _entityRegistry.GetEnabled<Dwelling>()) {
        var band = WorstBand(dwelling);
        if (band is { } color) {
          if (!_highlighted.TryGetValue(dwelling, out var current) || current != color) {
            _highlighter.HighlightSecondary(dwelling, color);
            _highlighted[dwelling] = color;
          }
        } else if (_highlighted.ContainsKey(dwelling)) {
          _highlighter.UnhighlightSecondary(dwelling);
          _highlighted.Remove(dwelling);
        }
      }
    }

    /// <summary>The band of the worst (longest) measured commute among a
    /// dwelling's adult occupants, or <c>null</c> when none has data — the worst
    /// commuter drives the house's colour. Children don't work, so they're
    /// ignored.</summary>
    private static Color? WorstBand(Dwelling dwelling) {
      var worst = float.NaN;
      foreach (var dweller in dwelling.AdultDwellers) {
        if (dweller.GetComponent<Worker>() is not { Employed: true } worker) {
          continue;
        }
        var cost = worker.GetComponent<CommuteCost>();
        if (cost == null || !cost.HasData) {
          continue;
        }
        if (float.IsNaN(worst) || cost.RoadDistance > worst) {
          worst = cost.RoadDistance;
        }
      }
      return CommuteBands.Resolve(worst);
    }

    #endregion

    #region Line / path draws

    private void DrawHouseLines(Dwelling dwelling) {
      var from = Center(dwelling);
      if (from is not { } origin) {
        return;
      }
      foreach (var dweller in dwelling.AdultDwellers) {
        if (dweller.GetComponent<Worker>() is not { Employed: true } worker) {
          continue;
        }
        var workplace = worker.Workplace;
        if (workplace == null || Center(workplace) is not { } target) {
          continue;
        }
        _lines.DrawSegment(origin, target, LineColor(worker));
        HighlightLink(workplace); // light up each workplace this house's beavers serve
      }
    }

    private void DrawWorkplaceLines(Workplace workplace) {
      var from = Center(workplace);
      if (from is not { } origin) {
        return;
      }
      foreach (var worker in workplace.AssignedWorkers) {
        var dweller = worker.GetComponent<Dweller>();
        if (dweller == null || !dweller.HasHome || Center(dweller.Home) is not { } target) {
          continue;
        }
        _lines.DrawSegment(origin, target, LineColor(worker));
        HighlightLink(dweller.Home); // light up each home this workplace draws from
      }
    }

    /// <summary>Highlight the selected beaver's home and workplace, and connect them
    /// with the walked road path — or, if that path can't be built, a straight line,
    /// so a beaver's commute always renders something.</summary>
    private void DrawBeaverPath(Worker worker) {
      var workplace = worker.Workplace;
      if (workplace == null) {
        return; // unemployed beaver: no commute to draw
      }
      var dweller = worker.GetComponent<Dweller>();
      if (dweller == null || !dweller.HasHome) {
        return;
      }
      var home = dweller.Home;
      HighlightLink(home);
      HighlightLink(workplace);
      var color = LineColor(worker);

      // Preferred: the actual walked path (one pathfind, off the optimizer's tick path).
      if (dweller.HomeAccess is { } start
          && workplace.GetEnabledComponent<Accessible>() is { } workplaceAccessible) {
        _cornerBuffer.Clear();
        if (workplaceAccessible.FindPathUnlimitedRange(start, _cornerBuffer, out _)
            && _cornerBuffer.Count >= 2) {
          _pathBuffer.Clear();
          foreach (var corner in _cornerBuffer) {
            _pathBuffer.Add(corner.Position + Vector3.up * LineHeight);
          }
          _lines.DrawPolyline(_pathBuffer, color);
          return;
        }
      }

      // Fallback: a straight connector between the two centres.
      if (Center(home) is { } a && Center(workplace) is { } b) {
        _lines.DrawSegment(a, b, color);
      }
    }

    #endregion

    #region Helpers

    private void ClearAll() {
      ClearHeatmap();
      ClearSelection();
    }

    /// <summary>Remove every dwelling secondary-highlight we applied.</summary>
    private void ClearHeatmap() {
      foreach (var dwelling in _highlighted.Keys) {
        _highlighter.UnhighlightSecondary(dwelling);
      }
      _highlighted.Clear();
    }

    /// <summary>Clear the current selection's lines and link highlights.</summary>
    private void ClearSelection() {
      _lines.Clear();
      foreach (var entity in _selectionHighlights) {
        _highlighter.UnhighlightSecondary(entity);
      }
      _selectionHighlights.Clear();
    }

    /// <summary>Light-blue-highlight an entity linked to the current selection
    /// (idempotent within a selection — re-highlighting the same entity is a no-op).</summary>
    private void HighlightLink(BaseComponent entity) {
      if (_selectionHighlights.Add(entity)) {
        _highlighter.HighlightSecondary(entity, LinkColor());
      }
    }

    /// <summary>The link-highlight colour — the vanilla power-network highlight
    /// colour (<c>MechanicalNodeHighlighterSpec.HighlightColor</c>), read once via
    /// the spec service so it matches exactly. Falls back to
    /// <see cref="FallbackLinkColor"/> (and warns) if that internal spec can't be
    /// resolved on a future game version.</summary>
    private Color LinkColor() {
      if (_linkColor is { } cached) {
        return cached;
      }
      var color = FallbackLinkColor;
      try {
        var specType = AccessTools.TypeByName(LinkColorSpecType);
        if (specType == null) {
          Debug.LogWarning($"[ShortCommute] '{LinkColorSpecType}' not found; using fallback link colour.");
        } else {
          var spec = AccessTools.Method(typeof(ISpecService), nameof(ISpecService.GetSingleSpec))
              .MakeGenericMethod(specType).Invoke(_specService, null);
          var property = AccessTools.Property(specType, "HighlightColor");
          if (spec != null && property != null) {
            color = (Color)property.GetValue(spec);
          }
        }
      } catch (System.Exception ex) {
        Debug.LogWarning($"[ShortCommute] Could not read vanilla highlight colour, "
                         + $"using fallback: {ex.Message}");
      }
      _linkColor = color;
      return color;
    }

    /// <summary>World-space line endpoint for a building: its grounded centre,
    /// lifted slightly. Null if the component carries no <see cref="BlockObjectCenter"/>.</summary>
    private static Vector3? Center(BaseComponent component) {
      var center = component.GetComponent<BlockObjectCenter>();
      return center == null ? null : center.WorldCenterGrounded + Vector3.up * LineHeight;
    }

    private static Color LineColor(Worker worker) {
      var cost = worker.GetComponent<CommuteCost>();
      return CommuteBands.ResolveOrNeutral(cost != null ? cost.RoadDistance : float.NaN);
    }

    #endregion

  }

}
