#if UNITY_EDITOR
//#define ENABLE_MINIWORLD_RAY_SELECTION
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor.Experimental.EditorVR.Modules;
using UnityEditor.Experimental.EditorVR.Utilities;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Experimental.EditorVR;
using UnityEngine.Experimental.EditorVR.Helpers;
using UnityEngine.Experimental.EditorVR.Menus;
using UnityEngine.Experimental.EditorVR.Modules;
using UnityEngine.Experimental.EditorVR.Tools;
using UnityEngine.Experimental.EditorVR.Utilities;
using UnityEngine.Experimental.EditorVR.Workspaces;
using UnityEngine.InputNew;
using UnityEngine.VR;

namespace UnityEditor.Experimental.EditorVR
{
	[InitializeOnLoad]
#if UNITY_EDITORVR
	[RequiresTag(kVRPlayerTag)]
	partial class EditorVR
	{
		public const HideFlags kDefaultHideFlags = HideFlags.DontSave;
		const string kVRPlayerTag = "VRPlayer";

		[SerializeField]
		private GameObject m_PlayerModelPrefab;

		[SerializeField]
		GameObject m_PreviewCameraPrefab;

		[SerializeField]
		ProxyExtras m_ProxyExtras;

		HighlightModule m_HighlightModule;
		ObjectPlacementModule m_ObjectPlacementModule;
		LockModule m_LockModule;
		SelectionModule m_SelectionModule;

		event Action m_SelectionChanged;

		IPreviewCamera m_CustomPreviewCamera;

		bool m_ControllersReady;

		void Awake()
		{
			ClearDeveloperConsoleIfNecessary();

			UpdateProjectFolders();
			UpdateHierarchyData();

			VRView.viewerPivot.parent = transform; // Parent the camera pivot under EditorVR
			if (VRSettings.loadedDeviceName == "OpenVR")
			{
				// Steam's reference position should be at the feet and not at the head as we do with Oculus
				VRView.viewerPivot.localPosition = Vector3.zero;
			}

			var hmdOnlyLayerMask = 0;
			if (m_PreviewCameraPrefab)
			{
				var go = U.Object.Instantiate(m_PreviewCameraPrefab);
				m_CustomPreviewCamera = go.GetComponentInChildren<IPreviewCamera>();
				if (m_CustomPreviewCamera != null)
				{
					VRView.customPreviewCamera = m_CustomPreviewCamera.previewCamera;
					m_CustomPreviewCamera.vrCamera = VRView.viewerCamera;
					hmdOnlyLayerMask = m_CustomPreviewCamera.hmdOnlyLayerMask;
				}
			}
			VRView.cullingMask = UnityEditor.Tools.visibleLayers | hmdOnlyLayerMask;

			InitializePlayerHandle();
			CreateDefaultActionMapInputs();
			CreateAllProxies();
			CreateDeviceDataForInputDevices();

			m_DragAndDropModule = U.Object.AddComponent<DragAndDropModule>(gameObject);

			CreateEventSystem();

			m_PixelRaycastModule = U.Object.AddComponent<PixelRaycastModule>(gameObject);
			m_PixelRaycastModule.ignoreRoot = transform;
			m_HighlightModule = U.Object.AddComponent<HighlightModule>(gameObject);
			m_LockModule = U.Object.AddComponent<LockModule>(gameObject);
			m_LockModule.updateAlternateMenu = (rayOrigin, o) => SetAlternateMenuVisibility(rayOrigin, o != null);
			ConnectInterfaces(m_LockModule);

			m_SelectionModule = U.Object.AddComponent<SelectionModule>(gameObject);
			m_SelectionModule.selected += SetLastSelectionRayOrigin; // when a selection occurs in the selection tool, call show in the alternate menu, allowing it to show/hide itself.
			m_SelectionModule.getGroupRoot = GetGroupRoot;
			ConnectInterfaces(m_SelectionModule);

			m_AllTools = U.Object.GetImplementationsOfInterface(typeof(ITool)).ToList();
			m_MainMenuTools = m_AllTools.Where(t => !IsPermanentTool(t)).ToList(); // Don't show tools that can't be selected/toggled
			m_AllWorkspaceTypes = U.Object.GetImplementationsOfInterface(typeof(IWorkspace)).ToList();

			UnityBrandColorScheme.sessionGradient = UnityBrandColorScheme.GetRandomGradient();
		}

		void ClearDeveloperConsoleIfNecessary()
		{
			var asm = Assembly.GetAssembly(typeof(Editor));
			var consoleWindowType = asm.GetType("UnityEditor.ConsoleWindow");

			EditorWindow window = null;
			foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
			{
				if (w.GetType() == consoleWindowType)
				{
					window = w;
					break;
				}
			}

			if (window)
			{
				var consoleFlagsType = consoleWindowType.GetNestedType("ConsoleFlags", BindingFlags.NonPublic);
				var names = Enum.GetNames(consoleFlagsType);
				var values = Enum.GetValues(consoleFlagsType);
				var clearOnPlayFlag = values.GetValue(Array.IndexOf(names, "ClearOnPlay"));

				var hasFlagMethod = consoleWindowType.GetMethod("HasFlag", BindingFlags.NonPublic | BindingFlags.Instance);
				var result = (bool)hasFlagMethod.Invoke(window, new[] { clearOnPlayFlag });

				if (result)
				{
					var logEntries = asm.GetType("UnityEditorInternal.LogEntries");
					var clearMethod = logEntries.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public);
					clearMethod.Invoke(null, null);
				}
			}
		}

		void OnSelectionChanged()
		{
			if (m_SelectionChanged != null)
				m_SelectionChanged();

			UpdateAlternateMenuOnSelectionChanged(m_LastSelectionRayOrigin);
		}

		IEnumerator Start()
		{
			// Delay until at least one proxy initializes
			bool proxyActive = false;
			while (!proxyActive)
			{
				foreach (var proxy in m_Proxies)
				{
					if (proxy.active)
					{
						proxyActive = true;
						break;
					}
				}

				yield return null;
			}

			m_ControllersReady = true;

			if (m_ProxyExtras)
			{
				var extraData = m_ProxyExtras.data;
				ForEachRayOrigin((proxy, pair, device, deviceData) =>
				{
					List<GameObject> prefabs;
					if (extraData.TryGetValue(pair.Key, out prefabs))
					{
						foreach (var prefab in prefabs)
						{
							var go = InstantiateUI(prefab);
							go.transform.SetParent(pair.Value, false);
						}
					}
				});
			}

			CreateSpatialSystem();

			m_ObjectPlacementModule = U.Object.AddComponent<ObjectPlacementModule>(gameObject);
			ConnectInterfaces(m_ObjectPlacementModule);

			SpawnActions();
			SpawnDefaultTools();
			AddPlayerModel();
			PrewarmAssets();

			// In case we have anything selected at start, set up manipulators, inspector, etc.
			EditorApplication.delayCall += OnSelectionChanged;

			// This will be the first call to update the player handle (input) maps, sorted by priority
			UpdatePlayerHandleMaps();
		}

		void OnEnable()
		{
			Selection.selectionChanged += OnSelectionChanged;
#if UNITY_EDITOR
			EditorApplication.hierarchyWindowChanged += OnHierarchyChanged;
			VRView.onGUIDelegate += OnSceneGUI;
			EditorApplication.projectWindowChanged += UpdateProjectFolders;
#endif
		}

		void OnDisable()
		{
			Selection.selectionChanged -= OnSelectionChanged;
#if UNITY_EDITOR
			EditorApplication.hierarchyWindowChanged -= OnHierarchyChanged;
			VRView.onGUIDelegate -= OnSceneGUI;
			EditorApplication.projectWindowChanged -= UpdateProjectFolders;
#endif
		}

		void OnSceneGUI(EditorWindow obj)
		{
			if (Event.current.type == EventType.ExecuteCommand)
			{
				if (m_PixelRaycastIgnoreListDirty)
				{
					m_PixelRaycastModule.UpdateIgnoreList();
					m_PixelRaycastIgnoreListDirty = false;
				}

				ForEachRayOrigin((proxy, pair, device, deviceData) =>
				{
					m_PixelRaycastModule.UpdateRaycast(pair.Value, m_EventCamera);
				});

#if ENABLE_MINIWORLD_RAY_SELECTION
				foreach (var rayOrigin in m_MiniWorldRays.Keys)
					m_PixelRaycastModule.UpdateRaycast(rayOrigin, m_EventCamera);
#endif

				// Queue up the next round
				m_UpdatePixelRaycastModule = true;

				Event.current.Use();
			}
		}

		void OnDestroy()
		{
			if (m_CustomPreviewCamera != null)
				U.Object.Destroy(((MonoBehaviour)m_CustomPreviewCamera).gameObject);

			PlayerHandleManager.RemovePlayerHandle(m_PlayerHandle);
		}

		void PrewarmAssets()
		{
			// HACK: Cannot async load assets in the editor yet, so to avoid a hitch let's spawn the menu immediately and then make it invisible
			foreach (var kvp in m_DeviceData)
			{
				var device = kvp.Key;
				var deviceData = kvp.Value;
				var mainMenu = deviceData.mainMenu;

				if (mainMenu == null)
				{
					mainMenu = SpawnMainMenu(typeof(MainMenu), device, false, out deviceData.mainMenuInput);
					deviceData.mainMenu = mainMenu;
					deviceData.menuHideFlags[mainMenu] = MenuHideFlags.Hidden;
					UpdatePlayerHandleMaps();
				}
			}
		}

		private void Update()
		{
			if (m_CustomPreviewCamera != null)
				m_CustomPreviewCamera.enabled = VRView.showDeviceView && VRView.customPreviewCamera != null;

#if UNITY_EDITOR
			// HACK: Send a custom event, so that OnSceneGUI gets called, which is requirement for scene picking to occur
			//		Additionally, on some machines it's required to do a delay call otherwise none of this works
			//		I noticed that delay calls were queuing up, so it was necessary to protect against that, so only one is processed
			if (m_UpdatePixelRaycastModule)
			{
				EditorApplication.delayCall += () =>
				{
					if (this != null) // Because this is a delay call, the component will be null when EditorVR closes
					{
						Event e = new Event();
						e.type = EventType.ExecuteCommand;
						VRView.activeView.SendEvent(e);
					}
				};

				m_UpdatePixelRaycastModule = false; // Don't allow another one to queue until the current one is processed
			}
#endif

			if (!m_ControllersReady)
				return;

			UpdateDefaultProxyRays();

			UpdateKeyboardMallets();

			ProcessInput();

			UpdateMenuVisibilityNearWorkspaces();
			UpdateMenuVisibilities();

			UpdateManipulatorVisibilites();
		}

		private void LogError(string error)
		{
			Debug.LogError(string.Format("EVR: {0}", error));
		}

		static GameObject GetGroupRoot(GameObject hoveredObject)
		{
			if (!hoveredObject)
				return null;

			var groupRoot = PrefabUtility.FindPrefabRoot(hoveredObject);
			if (groupRoot == hoveredObject)
				groupRoot = FindGroupRoot(hoveredObject.transform).gameObject;

			return groupRoot;
		}

		static Transform FindGroupRoot(Transform transform)
		{
			// Don't allow grouping selection for the player head, otherwise we'd select the EditorVRCamera
			if (transform.CompareTag(kVRPlayerTag))
				return transform;

			var parent = transform.parent;
			if (parent)
			{
				if (parent.GetComponent<Renderer>())
					return FindGroupRoot(parent);

				return parent;
			}

			return transform;
		}

		void AddPlayerModel()
		{
			var playerModel = U.Object.Instantiate(m_PlayerModelPrefab, U.Camera.GetMainCamera().transform, false).GetComponent<Renderer>();
			m_SpatialHashModule.spatialHash.AddObject(playerModel, playerModel.bounds);
		}

#if UNITY_EDITOR
		static EditorVR s_Instance;
		static InputManager s_InputManager;

		[MenuItem("Window/EditorVR %e", false)]
		public static void ShowEditorVR()
		{
			// Using a utility window improves performance by saving from the overhead of DockArea.OnGUI()
			VRView.GetWindow<VRView>(true, "EditorVR", true);
		}

		[MenuItem("Window/EditorVR %e", true)]
		public static bool ShouldShowEditorVR()
		{
			return PlayerSettings.virtualRealitySupported;
		}

		static EditorVR()
		{
			VRView.onEnable += OnEVREnabled;
			VRView.onDisable += OnEVRDisabled;

			if (!PlayerSettings.virtualRealitySupported)
				Debug.Log("<color=orange>EditorVR requires VR support. Please check Virtual Reality Supported in Edit->Project Settings->Player->Other Settings</color>");

#if !ENABLE_OVR_INPUT && !ENABLE_STEAMVR_INPUT && !ENABLE_SIXENSE_INPUT
			Debug.Log("<color=orange>EditorVR requires at least one partner (e.g. Oculus, Vive) SDK to be installed for input. You can download these from the Asset Store or from the partner's website</color>");
#endif

			// Add EVR tags and layers if they don't exist
			var tags = TagManager.GetRequiredTags();
			var layers = TagManager.GetRequiredLayers();

			foreach (var tag in tags)
				TagManager.AddTag(tag);

			foreach (var layer in layers)
				TagManager.AddLayer(layer);
		}

		private static void OnEVREnabled()
		{
			InitializeInputManager();
			s_Instance = U.Object.CreateGameObjectWithComponent<EditorVR>();
		}

		private static void InitializeInputManager()
		{
			// HACK: InputSystem has a static constructor that is relied upon for initializing a bunch of other components, so
			// in edit mode we need to handle lifecycle explicitly
			InputManager[] managers = Resources.FindObjectsOfTypeAll<InputManager>();
			foreach (var m in managers)
			{
				U.Object.Destroy(m.gameObject);
			}

			managers = Resources.FindObjectsOfTypeAll<InputManager>();
			if (managers.Length == 0)
			{
				// Attempt creating object hierarchy via an implicit static constructor call by touching the class
				InputSystem.ExecuteEvents();
				managers = Resources.FindObjectsOfTypeAll<InputManager>();

				if (managers.Length == 0)
				{
					typeof(InputSystem).TypeInitializer.Invoke(null, null);
					managers = Resources.FindObjectsOfTypeAll<InputManager>();
				}
			}
			Assert.IsTrue(managers.Length == 1, "Only one InputManager should be active; Count: " + managers.Length);

			s_InputManager = managers[0];
			s_InputManager.gameObject.hideFlags = kDefaultHideFlags;
			U.Object.SetRunInEditModeRecursively(s_InputManager.gameObject, true);

			// These components were allocating memory every frame and aren't currently used in EditorVR
			U.Object.Destroy(s_InputManager.GetComponent<JoystickInputToEvents>());
			U.Object.Destroy(s_InputManager.GetComponent<MouseInputToEvents>());
			U.Object.Destroy(s_InputManager.GetComponent<KeyboardInputToEvents>());
			U.Object.Destroy(s_InputManager.GetComponent<TouchInputToEvents>());
		}

		private static void OnEVRDisabled()
		{
			U.Object.Destroy(s_Instance.gameObject);
			U.Object.Destroy(s_InputManager.gameObject);
		}
#endif
	}
#else
	internal class NoEditorVR
	{
		const string kShowCustomEditorWarning = "EditorVR.ShowCustomEditorWarning";

		static NoEditorVR()
		{
			if (EditorPrefs.GetBool(kShowCustomEditorWarning, true))
			{
				var message = "EditorVR requires a custom editor build. Please see https://blogs.unity3d.com/2016/12/15/editorvr-experimental-build-available-today/";
				var result = EditorUtility.DisplayDialogComplex("Custom Editor Build Required", message, "Download", "Ignore", "Remind Me Again");
				switch (result)
				{
					case 0:
						Application.OpenURL("http://rebrand.ly/EditorVR-build");
						break;
					case 1:
						EditorPrefs.SetBool(kShowCustomEditorWarning, false);
						break;
					case 2:
						Debug.Log("<color=orange>" + message + "</color>");
						break;
				}
			}
		}
	}
#endif
}
#endif
