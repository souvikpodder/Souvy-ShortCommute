using Timberborn.CoreUI;
using Timberborn.SingletonSystem;
using Timberborn.TooltipSystem;
using Timberborn.UILayoutSystem;
using UnityEngine;
using UnityEngine.UIElements;

namespace SouvyShortCommute.Overlay {

  /// <summary>
  /// Top-right toggle button that turns "commute analysis mode" on and off.
  /// Uses vanilla Timberborn <c>Common/SquareToggle</c> and loads UI/commute_analysis_icon.png.
  /// </summary>
  public sealed class CommuteOverlayToggle : ILoadableSingleton {

    #region Constants

    private const string ToggleAsset = "Common/SquareToggle";
    private const string ShowTooltip = "Show commute analysis";
    private const string HideTooltip = "Hide commute analysis";
    private const int ButtonOrder = 10;

    #endregion

    #region Dependencies

    private readonly VisualElementLoader _visualElementLoader;
    private readonly UILayout _uiLayout;
    private readonly ITooltipRegistrar _tooltipRegistrar;
    private readonly EventBus _eventBus;

    #endregion

    #region State

    private VisualElement _root = null!;
    private Toggle _toggle = null!;
    private bool _enabled;

    /// <summary>Whether commute analysis mode is currently active.</summary>
    public bool Enabled => _enabled;

    #endregion

    public CommuteOverlayToggle(VisualElementLoader visualElementLoader, UILayout uiLayout,
        ITooltipRegistrar tooltipRegistrar, EventBus eventBus) {
      _visualElementLoader = visualElementLoader;
      _uiLayout = uiLayout;
      _tooltipRegistrar = tooltipRegistrar;
      _eventBus = eventBus;
    }

    #region Lifecycle

    /// <inheritdoc />
    public void Load() {
      _root = _visualElementLoader.LoadVisualElement(ToggleAsset);
      _toggle = _root.Q<Toggle>("Toggle");

      ApplyCustomIcon();

      _tooltipRegistrar.Register(_root, () => _enabled ? HideTooltip : ShowTooltip);
      
      _toggle.RegisterValueChangedCallback(evt => {
        _enabled = evt.newValue;
      });

      _toggle.RegisterCallback<ClickEvent>(evt => {
        _enabled = _toggle.value;
      });

      _eventBus.Register(this);
    }

    private void ApplyCustomIcon() {
      try {
        string iconPath = FindIconPath();

        if (!string.IsNullOrEmpty(iconPath) && System.IO.File.Exists(iconPath)) {
          byte[] fileData = System.IO.File.ReadAllBytes(iconPath);
          Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
          
          var imgConvType = System.Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
          if (imgConvType != null) {
            var loadImageMethod = imgConvType.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
            if (loadImageMethod != null && (bool)loadImageMethod.Invoke(null, new object[] { texture, fileData })) {
              texture.filterMode = FilterMode.Bilinear;

              // Hide default checkmark label/text if present
              VisualElement checkmark = _toggle.Q<VisualElement>("Checkmark") ?? _toggle.Q<VisualElement>("unity-checkmark");
              if (checkmark == null && _toggle.childCount > 0) {
                checkmark = _toggle[0];
              }

              if (checkmark != null) {
                checkmark.style.backgroundImage = null;
                if (checkmark is Label lbl) {
                  lbl.text = "";
                }
              }

              // Create perfectly centered icon element
              VisualElement iconElement = new VisualElement();
              iconElement.pickingMode = PickingMode.Ignore;
              iconElement.style.backgroundImage = new StyleBackground(texture);
              iconElement.style.position = Position.Absolute;
              iconElement.style.left = 0;
              iconElement.style.right = 0;
              iconElement.style.top = 0;
              iconElement.style.bottom = 0;
              iconElement.style.alignSelf = Align.Center;
              iconElement.style.justifyContent = Justify.Center;
              iconElement.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;

              _toggle.Add(iconElement);
              return;
            }
          }
        }
      } catch (System.Exception ex) {
        Debug.LogWarning($"[Souvy-ShortCommute] Failed to load PNG icon: {ex.Message}");
      }
    }

    private string FindIconPath() {
      string userDocs = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
      string iconPath = System.IO.Path.Combine(userDocs, "Timberborn", "Mods", "Souvy-ShortCommute", "version-1.1.0", "UI", "commute_analysis_icon.png");
      if (System.IO.File.Exists(iconPath)) {
        return iconPath;
      }

      string execAssembly = System.Reflection.Assembly.GetExecutingAssembly().Location;
      if (!string.IsNullOrEmpty(execAssembly)) {
        string dir = System.IO.Path.GetDirectoryName(execAssembly) ?? "";
        string fallback = System.IO.Path.Combine(dir, "UI", "commute_analysis_icon.png");
        if (System.IO.File.Exists(fallback)) return fallback;
      }
      return null;
    }

    /// <summary>Re-attach the button whenever the primary UI is (re)built.</summary>
    [OnEvent]
    public void OnShowPrimaryUI(ShowPrimaryUIEvent showPrimaryUIEvent) {
      _uiLayout.AddTopRightButton(_root, ButtonOrder);
    }

    #endregion

  }

}
