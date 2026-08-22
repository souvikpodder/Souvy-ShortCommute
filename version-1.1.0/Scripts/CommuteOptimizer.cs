using System.Collections.Generic;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.BlockingSystem;
using Timberborn.Buildings;
using Timberborn.DwellingSystem;
using Timberborn.GameDistricts;
using Timberborn.SingletonSystem;
using Timberborn.TickSystem;
using Timberborn.TimeSystem;
using Timberborn.WorkSystem;

namespace SouvyShortCommute {

  /// <summary>
  /// Per-district home optimizer. Each in-game day it reassigns employed
  /// beavers' <em>homes</em> (never their jobs) so each lives in a dwelling as
  /// close as possible by road distance to their workplace — via a direct move
  /// into a closer free dwelling, or a home swap that lowers the total commute.
  ///
  /// <para>Three properties keep per-frame cost flat:</para>
  /// <list type="number">
  ///   <item><b>Cheap per action.</b> Road distances are explored once per
  ///   <em>dwelling</em> (rooted at the dwelling, giving its distance to every
  ///   workplace) and cached for the pass (<see cref="DwellingRowCache"/>).</item>
  ///   <item><b>Fill-free swaps.</b> A swap's four distances are column lookups
  ///   in two already-built rows (the mover's home and the target), so swap
  ///   evaluation never triggers a fresh exploration.</item>
  ///   <item><b>Hard per-tick budget.</b> A tick performs at most
  ///   <see cref="MaxFillsPerTick"/> fresh explorations; a worker that needs
  ///   more is <em>deferred</em> — re-queued untouched and resumed next tick,
  ///   when its earlier explorations are still cached.</item>
  /// </list>
  ///
  /// <para>v1 scope: a daily full pass (the queue is rebuilt on day start).
  /// Event-driven scheduling and finer distance invalidation are future
  /// refinements.</para>
  /// </summary>
  public class CommuteOptimizer : TickableComponent, IAwakableComponent, IFinishedStateListener {

    #region Tuning

    /// <summary>Minimum road-distance reduction for a beaver to bother moving.</summary>
    private const float MinMoveImprovement = 4f;

    /// <summary>Minimum combined improvement for a two-beaver home swap.</summary>
    private const float MinSwapImprovement = 10f;

    /// <summary>Hard ceiling on fresh dwelling explorations per tick.</summary>
    private const int MaxFillsPerTick = 2;

    /// <summary>Safety cap on cheap (cache-hit) workers processed per tick.</summary>
    private const int MaxWorkersPerTick = 64;

    /// <summary>Dwellings within this many road-tiles of a filled one are treated as
    /// the same block and share its distance row — one fill per block instead of per
    /// dwelling. Kept below typical commute scale so the bounded approximation only
    /// affects near-threshold moves. Set 0 to disable clustering (per-dwelling fills).</summary>
    private const float ClusterRadius = 10f;

    #endregion

    #region State

    private readonly EventBus _eventBus;
    private DistrictCenter _districtCenter = null!;
    private readonly List<Worker> _pending = new();

    /// <summary>Distinct workplaces of the district's employed adults this pass — the
    /// target set every dwelling row is measured against.</summary>
    private readonly List<Workplace> _workplaces = new();

    private readonly DwellingRowCache _rows = new(MaxFillsPerTick, ClusterRadius);

    /// <summary>Per-worker scratch: candidate dwellings with their distance to the
    /// worker's workplace, reused to avoid per-call allocation.</summary>
    private readonly List<(Dwelling dwelling, float distance)> _candidates = new();

    #endregion

    public CommuteOptimizer(EventBus eventBus) => _eventBus = eventBus;

    #region Lifecycle

    public void Awake() => _districtCenter = GetComponent<DistrictCenter>();

    public void OnEnterFinishedState() => _eventBus.Register(this);

    public void OnExitFinishedState() => _eventBus.Unregister(this);

    [OnEvent]
    public void OnDaytimeStart(DaytimeStartEvent daytimeStart) => BeginPass();

    public void Start() => BeginPass();

    #endregion

    #region Pass scheduling

    /// <summary>
    /// Start a fresh rebalance pass: drop stale distances, re-enqueue employed
    /// adults, and recompute the distinct-workplace target set.
    /// </summary>
    private void BeginPass() {
      _rows.Clear();
      _workplaces.Clear();
      foreach (var worker in _districtCenter.DistrictPopulation.Adults
                   .Where(beaver => beaver && beaver.GameObject != null && beaver.Enabled)
                   .Select(beaver => beaver.GetComponent<Worker>())
                   .Where(worker => worker is { Employed: true })) {
        if (!_pending.Contains(worker)) {
          _pending.Add(worker);
        }
        var workplace = worker.Workplace;
        if (workplace != null && !_workplaces.Contains(workplace)) {
          _workplaces.Add(workplace);
        }
      }
    }

    /// <inheritdoc />
    public override void Tick() {
      if (_pending.Count == 0) {
        return;
      }
      _rows.ResetTickCounter();
      var dwellings = GetActiveDwellings(_districtCenter);
      _rows.SetClusterDwellings(dwellings);
      var districtOverpopulated = DistrictOverpopulatedByAdults(_districtCenter, dwellings);

      var processed = 0;
      while (_pending.Count > 0 && processed < MaxWorkersPerTick) {
        var worker = _pending[0];

        // Null/unemployed workers are dropped cheaply (they cost no exploration).
        if (worker is not { Employed: true }) {
          _pending.RemoveAt(0);
          processed++;
          continue;
        }

        // A deferred worker ran out of exploration budget mid-evaluation. Leave it
        // at the front (untouched — no move was applied) and resume next tick, when
        // the rows it already built are cached.
        TryImprove(worker, dwellings, districtOverpopulated, out var deferred);
        if (deferred) {
          break;
        }
        _pending.RemoveAt(0);
        processed++;
      }
    }

    #endregion

    #region Improvement logic

    /// <summary>
    /// Try to move <paramref name="worker"/> to a closer home (direct move or
    /// swap). Returns true if a reassignment was applied. Sets
    /// <paramref name="deferred"/> when ranking needs a dwelling exploration the
    /// per-tick budget can't afford — nothing is changed and the caller should
    /// retry the worker next tick.
    /// </summary>
    private bool TryImprove(Worker worker, List<Dwelling> dwellings, bool districtOverpopulated,
        out bool deferred) {
      deferred = false;

      var workplace = worker.Workplace;
      if (workplace == null) {
        return false;
      }
      // Correctness guard (not optimization): never place a beaver in a home in
      // THIS district when their workplace belongs to another district. Our worker
      // queue is a snapshot taken at day start; if a mid-pass road edit split the
      // district, that snapshot can be stale and name a worker whose job is now
      // elsewhere. Assigning them a home here would create an invalid cross-district
      // pairing. Skip (drop from the queue) — the owning district's optimizer handles
      // them, and our next pass rebuilds the queue from current membership. A stale
      // *distance* is harmless; a stale *district* is not.
      if (workplace.GetComponent<DistrictBuilding>()?.District != _districtCenter) {
        return false;
      }
      var dweller = worker.GetComponent<Dweller>();
      if (dweller == null) {
        return false;
      }
      var home = dweller.Home;

      // Current commute: the road distance from the worker's home to their job — a
      // column lookup in the home dwelling's row.
      var currentDistance = float.MaxValue;
      if (dweller.HasHome && home != null) {
        var homeRow = _rows.TryGetRow(home, _workplaces);
        if (homeRow == null) {
          deferred = true;
          return false;
        }
        if (homeRow.Distances.TryGetValue(workplace, out var hd)) {
          currentDistance = hd;
        }
      }
      // In an adult-overpopulated district, allow more aggressive moves (into child
      // slots / displacing children) to relieve the overflow.
      var aggressive = districtOverpopulated && home is { OverpopulatedByAdults: true };

      // Rank candidate dwellings by distance to this workplace. Every active
      // dwelling's row is needed to find the nearest; if any can't be built within
      // this tick's budget, defer the whole worker (rows built so far stay cached).
      _candidates.Clear();
      foreach (var dwelling in dwellings) {
        var row = _rows.TryGetRow(dwelling, _workplaces);
        if (row == null) {
          deferred = true;
          return false;
        }
        if (row.Distances.TryGetValue(workplace, out var distance)) {
          _candidates.Add((dwelling, distance));
        }
      }
      _candidates.Sort(static (a, b) => a.distance.CompareTo(b.distance));

      foreach (var (dwelling, distance) in _candidates) {
        if (dwelling == home) {
          RecordCommute(worker, currentDistance); // staying put: own home is already closest
          return false; // reached our own home in nearest-first order: nothing closer remains
        }
        var improvement = currentDistance - distance;
        if (improvement < MinMoveImprovement) {
          RecordCommute(worker, currentDistance); // no worthwhile move: keep current commute
          return false; // sorted nearest-first, so every remaining dwelling is at least as far
        }

        // Direct move into a closer dwelling with capacity.
        if (dwelling.HasFreeSlots && (dwelling.FreeAdultSlots > 0 || aggressive)) {
          if (dweller.HasHome) {
            home!.UnassignDweller(dweller);
          }
          dwelling.AssignDweller(dweller);
          RecordCommute(worker, distance); // moved: new commute is this candidate's distance
          return true;
        }

        if (!dweller.HasHome) {
          continue; // no home to offer in a swap
        }

        // Swap: hand our home to whoever in the target benefits most (or is least
        // inconvenienced). Fill-free — all distances are lookups in rows we have.
        var partner = FindSwapCandidate(home!, dwelling, improvement, aggressive);
        if (partner != null) {
          // home's row is cached (built when we read currentDistance above), so the
          // partner's post-swap commute — they take our home — is a free lookup.
          var homeRow = _rows.TryGetRow(home!, _workplaces);
          SwapHomes(dweller, partner);
          RecordCommute(worker, distance); // worker now lives in the target dwelling
          if (homeRow != null) {
            RecordPartnerCommute(partner, homeRow); // partner now lives in our old home
          }
          return true;
        }
      }
      RecordCommute(worker, currentDistance); // exhausted candidates, nothing applied
      return false;
    }

    /// <summary>
    /// Pick the occupant of <paramref name="target"/> who should swap into
    /// <paramref name="home"/>. Employed occupants are scored by their own
    /// commute change; an unemployed occupant is a zero-improvement fallback. A
    /// swap is accepted only when it strictly lowers total commute. All four
    /// distances are column lookups in the (already-built) home and target rows,
    /// so this triggers no fresh exploration.
    /// </summary>
    private Dweller? FindSwapCandidate(Dwelling home, Dwelling target, float workerImprovement,
        bool aggressive) {
      // Both rows are already cached: 'home' from the current-commute lookup, 'target'
      // from the candidate ranking. TryGetRow returns them without a new fill.
      var homeRow = _rows.TryGetRow(home, _workplaces);
      var targetRow = _rows.TryGetRow(target, _workplaces);
      if (homeRow == null || targetRow == null) {
        return null;
      }

      Dweller? best = null;
      var bestImprovement = float.MinValue;

      var occupants = (aggressive
          ? target.AdultDwellers.Concat(target.ChildDwellers)
          : target.AdultDwellers)
          .Where(d => d && d.GameObject != null && d.Enabled);

      foreach (var occupant in occupants) {
        var occupantWorker = occupant.GetComponent<Worker>();
        var occupantWorkplace = occupantWorker?.Workplace;
        if (occupantWorker is not { Employed: true } || occupantWorkplace == null) {
          // Neutral: no commute to worsen. Use only if nothing positive turns up.
          if (bestImprovement < 0f) {
            best = occupant;
            bestImprovement = 0f;
          }
          continue;
        }

        // Their commute now (living in target) vs. if they took our home — both are
        // lookups of their job column in the target and home rows.
        if (!targetRow.Distances.TryGetValue(occupantWorkplace, out var occupantCurrent)
            || !homeRow.Distances.TryGetValue(occupantWorkplace, out var occupantNew)) {
          continue;
        }
        var occupantImprovement = occupantCurrent - occupantNew;

        if ((occupantImprovement >= MinMoveImprovement
             || workerImprovement + occupantImprovement >= MinSwapImprovement)
            && occupantImprovement > bestImprovement) {
          best = occupant;
          bestImprovement = occupantImprovement;
        }
      }
      return best;
    }

    #endregion

    #region Overlay data

    /// <summary>
    /// Stamp <paramref name="worker"/>'s <see cref="CommuteCost"/> with its freshly
    /// settled home→workplace road distance, for the commute overlay to read.
    /// <see cref="float.MaxValue"/> (no home / unreachable) is recorded as "no data".
    /// Display-only and never read back by the optimizer; a worker missing the
    /// component (it shouldn't — the decorator adds it to every <see cref="Worker"/>)
    /// is simply skipped rather than crashing the tick over a cosmetic value.
    /// </summary>
    private static void RecordCommute(Worker worker, float roadDistance) {
      var cost = worker.GetComponent<CommuteCost>();
      if (cost == null) {
        return;
      }
      if (roadDistance >= float.MaxValue) {
        cost.Clear();
      } else {
        cost.Set(roadDistance);
      }
    }

    /// <summary>
    /// After a swap, stamp the partner's new commute: they have moved into
    /// <paramref name="newHome"/>, so their home→job distance is a lookup of their
    /// workplace column in that home's already-built row. An unemployed partner, or
    /// a child swapped in aggressive mode (no <see cref="Worker"/>), records nothing.
    /// </summary>
    private static void RecordPartnerCommute(Dweller partner, DwellingRow newHome) {
      var partnerWorker = partner.GetComponent<Worker>();
      if (partnerWorker == null) {
        return;
      }
      var partnerWorkplace = partnerWorker.Workplace;
      var distance = float.MaxValue;
      if (partnerWorkplace != null
          && newHome.Distances.TryGetValue(partnerWorkplace, out var d)) {
        distance = d;
      }
      RecordCommute(partnerWorker, distance);
    }

    #endregion

    #region District helpers

    // Active = enabled, not paused, and not blocked. A blocked dwelling
    // (flooded/obstructed) auto-evicts its dwellers and won't accept assignment,
    // so it must never enter the candidate set.
    //
    // We intentionally do NOT apply the vanilla AutoAssignableDwelling.CanMoveIn /
    // CanAssignDweller check. Despite the name it isn't an eligibility/lock test —
    // it's the auto-assigner's slot-balancing heuristic (it rejects a move unless
    // the target is sufficiently emptier than the current home), which is
    // orthogonal to commute and would block our improving moves. (Bob's HousingOptimize
    // calls it only because it evicts everyone first, making it a no-op.) It's also
    // internal. The blocked check below is the only part of vanilla's gate worth taking.
    private static List<Dwelling> GetActiveDwellings(DistrictCenter center) =>
        center.DistrictBuildingRegistry.GetEnabledBuildingsInstant<Dwelling>()
            .Where(dwelling => dwelling.GetComponent<PausableBuilding>() is not { Paused: true }
                               && dwelling.GetComponent<BlockableObject>() is not { IsUnblocked: false })
            .ToList();

    private static bool DistrictOverpopulatedByAdults(DistrictCenter center, List<Dwelling> dwellings) =>
        CountAdultBeavers(center) - dwellings.Sum(dwelling => dwelling.AdultSlots) > 0;

    private static int CountAdultBeavers(DistrictCenter center) =>
        center.DistrictPopulation.Adults
            .Where(beaver => beaver && beaver.GameObject != null && beaver.Enabled)
            .Count(beaver => beaver.HasComponent<Dweller>());

    private static void SwapHomes(Dweller a, Dweller b) {
      var homeA = a.Home;
      var homeB = b.Home;
      homeA.UnassignDweller(a);
      homeB.UnassignDweller(b);
      homeA.AssignDweller(b);
      homeB.AssignDweller(a);
    }

    #endregion

  }

}
