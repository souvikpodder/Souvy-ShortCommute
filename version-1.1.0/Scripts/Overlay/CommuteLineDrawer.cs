using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SouvyShortCommute.Overlay {

  /// <summary>
  /// Draws coloured world-space lines (house→workplace connections and the
  /// beaver commute path) using pooled Unity <see cref="LineRenderer"/>s.
  ///
  /// <para><b>The one unproven primitive.</b> Timberborn has no line renderer of
  /// its own and builds every material from an asset spec; we ship no asset
  /// bundle. So we create the material at runtime from the URP unlit shader
  /// (<c>Shader.Find("Universal Render Pipeline/Unlit")</c>) and colour each line
  /// solid via <see cref="Material.color"/> — deliberately not the vertex-colour
  /// gradient path, which URP/Unlit ignores. One material is cached per distinct
  /// band colour. If this turns out not to render under the game's URP setup, the
  /// fallback is to drop lines and secondary-highlight the connected entities
  /// instead (see <c>docs/commute-overlay-plan.md</c>).</para>
  ///
  /// <para>Lines are pooled and reused: <see cref="Clear"/> disables the active
  /// set without destroying it, and the next draw pass re-enables from the pool.
  /// All renderers parent under one container <see cref="GameObject"/>.</para>
  /// </summary>
  public sealed class CommuteLineDrawer {

    #region Constants

    /// <summary>World-space width of a drawn line, in tiles.</summary>
    private const float LineWidth = 0.25f;

    /// <summary>URP unlit shader name — present in any URP build.</summary>
    private const string UnlitShader = "Universal Render Pipeline/Unlit";

    #endregion

    #region State

    private readonly List<LineRenderer> _pool = new();
    private readonly Dictionary<Color, Material> _materials = new();
    private GameObject _container = null!;
    private Shader _shader = null!;
    private int _active;
    private bool _initialized;

    #endregion

    #region API

    /// <summary>Hide all currently drawn lines (pooled, not destroyed).</summary>
    public void Clear() {
      for (var i = 0; i < _active; i++) {
        _pool[i].enabled = false;
      }
      _active = 0;
    }

    /// <summary>Draw a single straight segment from <paramref name="from"/> to
    /// <paramref name="to"/> in <paramref name="color"/>.</summary>
    public void DrawSegment(Vector3 from, Vector3 to, Color color) {
      var line = Next(color);
      line.positionCount = 2;
      line.SetPosition(0, from);
      line.SetPosition(1, to);
    }

    /// <summary>Draw a polyline through <paramref name="points"/> (e.g. the
    /// corners of a walked path) in <paramref name="color"/>. A run of fewer than
    /// two points is nothing to draw.</summary>
    public void DrawPolyline(IReadOnlyList<Vector3> points, Color color) {
      if (points.Count < 2) {
        return;
      }
      var line = Next(color);
      line.positionCount = points.Count;
      for (var i = 0; i < points.Count; i++) {
        line.SetPosition(i, points[i]);
      }
    }

    #endregion

    #region Pool

    private LineRenderer Next(Color color) {
      EnsureInitialized();
      LineRenderer line;
      if (_active < _pool.Count) {
        line = _pool[_active];
      } else {
        line = CreateLine();
        _pool.Add(line);
      }
      _active++;
      line.enabled = true;
      line.material = MaterialFor(color);
      // Solid colour via the material; start/end kept in sync for any shader that
      // does honour vertex colour, harmless for URP/Unlit which does not.
      line.startColor = color;
      line.endColor = color;
      return line;
    }

    private LineRenderer CreateLine() {
      var gameObject = new GameObject("CommuteLine");
      gameObject.transform.SetParent(_container.transform, worldPositionStays: false);
      var line = gameObject.AddComponent<LineRenderer>();
      line.useWorldSpace = true;
      line.widthMultiplier = LineWidth;
      line.numCapVertices = 2;
      line.numCornerVertices = 2;
      line.alignment = LineAlignment.View;
      line.shadowCastingMode = ShadowCastingMode.Off;
      line.receiveShadows = false;
      line.lightProbeUsage = LightProbeUsage.Off;
      return line;
    }

    private Material MaterialFor(Color color) {
      if (!_materials.TryGetValue(color, out var material)) {
        // NB: lines are depth-tested, so terrain/buildings occlude them. Drawing
        // them always-on-top is NOT achievable via this material — URP's Unlit
        // shader ignores the _ZTest property (verified: setting it had no effect).
        // A true overlay needs either GL immediate-mode drawing with a depth-
        // ignoring material, or a bundled custom shader (which would mean shipping
        // an asset bundle). See docs/commute-overlay-plan.md.
        material = new Material(_shader) { color = color };
        _materials[color] = material;
      }
      return material;
    }

    private void EnsureInitialized() {
      if (_initialized) {
        return;
      }
      _container = new GameObject("ShortCommute.CommuteLines");
      _shader = Shader.Find(UnlitShader);
      if (_shader == null) {
        // Fail loudly: a missing shader means every line would silently render as
        // magenta/invisible. Surface it so the fallback decision is explicit.
        Debug.LogError($"[ShortCommute] Shader '{UnlitShader}' not found — commute "
                       + "lines cannot render. Falling back is required.");
      }
      _initialized = true;
    }

    #endregion

  }

}
