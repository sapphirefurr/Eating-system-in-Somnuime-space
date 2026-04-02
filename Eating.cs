using SomniumSpace.Bridge;
using SomniumSpace.Bridge.Player;
using SomniumSpace.Worlds.Snacks;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace SomniumSpace.Worlds.Snacks
{
    public class GrabAndEat : MonoBehaviour
    {
        [Header("Table Settings")]
        [Tooltip("Where the food teleports back to after being eaten.")]
        [SerializeField] private Transform tableSpawnPoint;

        [Header("Mesh")]
        [Tooltip("The MeshRenderer on the food to hide/show.")]
        [SerializeField] private MeshRenderer foodMeshRenderer;
        [Tooltip("How long in seconds before the food reappears on the table.")]
        [SerializeField] private float respawnDelay = 1f;

        [Header("Mouth Detection")]
        [Tooltip("Distance from head/camera to count as eating in metres.")]
        [SerializeField] private float mouthRadius = 0.3f;

        [Header("Networking")]
        [Tooltip("The NetworkEnableObject on this same GameObject.")]
        [SerializeField] private NetworkEnableObject networkEnableObject;

        [Header("Optional Feedback")]
        [SerializeField] private AudioSource audioSource;
        [SerializeField] private AudioClip eatSFX;
        [SerializeField] private ParticleSystem eatParticles;

        private XRGrabInteractable _grabInteractable;
        private Rigidbody _rigidbody;
        private bool _isGrabbed;

        // FIX: _hasBeenEaten now only resets in ShowMesh, not in OnNetworkEatReceived.
        // This prevents a second eat event firing during the respawn delay.
        private bool _hasBeenEaten;

        // FIX: tracks whether a respawn is already pending, so multiple OnNetworkEatReceived
        // calls (from multiple clients all invoking ShowMesh) don't stack up.
        private bool _respawnPending;

        private Transform _localPlayerHead;
        private bool _headSearchDone;
        private Camera _mainCamera;

        private float _debugLogTimer;
        private const float DEBUG_LOG_INTERVAL = 1f;

        private float _mouthCheckTimer;
        private const float MOUTH_CHECK_INTERVAL = 0.1f;

        private bool _audioPlayed;

        private void Awake()
        {
            _grabInteractable = GetComponent<XRGrabInteractable>();

            if (_grabInteractable == null)
                Debug.LogWarning("GrabAndEat: No XRGrabInteractable found.");
            else
                Debug.Log("GrabAndEat: Awake OK - XRGrabInteractable found.");

            if (networkEnableObject == null)
                Debug.LogWarning("GrabAndEat: NetworkEnableObject is NOT assigned in Inspector!");
            else
                Debug.Log("GrabAndEat: Awake OK - NetworkEnableObject found.");

            _rigidbody = GetComponent<Rigidbody>();

            if (foodMeshRenderer == null)
                foodMeshRenderer = GetComponent<MeshRenderer>();

            if (foodMeshRenderer == null)
                Debug.LogWarning("GrabAndEat: No MeshRenderer found - assign it in the Inspector.");

            if (_grabInteractable != null)
            {
                _grabInteractable.selectEntered.AddListener(OnGrabbed);
                _grabInteractable.selectExited.AddListener(OnReleased);
            }
        }

        private void OnEnable()
        {
            TryGetReferences();

            var container = SomniumBridge.PlayersContainer;
            if (container != null)
                container.OnLocalPlayerAdded += OnLocalPlayerAdded;
        }

        private void OnDisable()
        {
            var container = SomniumBridge.PlayersContainer;
            if (container != null)
                container.OnLocalPlayerAdded -= OnLocalPlayerAdded;
        }

        private void OnLocalPlayerAdded(ISomniumPlayer player)
        {
            _localPlayerHead = player?.References?.Body?.Head;
            _headSearchDone = _localPlayerHead != null;
            Debug.Log("GrabAndEat: OnLocalPlayerAdded - head found: " + _headSearchDone);
        }

        private void TryGetReferences()
        {
            if (!_headSearchDone)
            {
                var container = SomniumBridge.PlayersContainer;
                if (container != null)
                {
                    var localPlayer = container.LocalPlayer;
                    if (localPlayer != null)
                    {
                        _localPlayerHead = localPlayer.References?.Body?.Head;
                        _headSearchDone = _localPlayerHead != null;
                        Debug.Log("GrabAndEat: TryGetReferences - head found: " + _headSearchDone);
                    }
                    else
                    {
                        Debug.Log("GrabAndEat: TryGetReferences - LocalPlayer is null, will retry on grab.");
                    }
                }
            }

            if (_mainCamera == null && Camera.main != null)
            {
                _mainCamera = Camera.main;
                Debug.Log("GrabAndEat: TryGetReferences - Camera.main found: " + _mainCamera.name);
            }
        }

        private void OnGrabbed(SelectEnterEventArgs args)
        {
            _isGrabbed = true;
            _debugLogTimer = 0f;
            _mouthCheckTimer = 0f;
            TryGetReferences();
            Debug.Log("GrabAndEat: Object GRABBED."
                + " | Bridge head: " + (_localPlayerHead != null)
                + " | Camera.main: " + (_mainCamera != null)
                + " | hasBeenEaten: " + _hasBeenEaten
                + " | mouthRadius: " + mouthRadius);
        }

        private void OnReleased(SelectExitEventArgs args)
        {
            _isGrabbed = false;
            Debug.Log("GrabAndEat: Object RELEASED.");
        }

        private void Update()
        {
            if (!_isGrabbed) return;
            if (_hasBeenEaten) return;

            _mouthCheckTimer += Time.deltaTime;
            if (_mouthCheckTimer < MOUTH_CHECK_INTERVAL) return;
            _mouthCheckTimer = 0f;

            if (_mainCamera == null && Camera.main != null)
                _mainCamera = Camera.main;

            Vector3 mouthPos;
            string mouthSource;

            if (_localPlayerHead != null)
            {
                mouthPos = _localPlayerHead.position;
                mouthSource = "BridgeHead";
            }
            else if (_mainCamera != null)
            {
                mouthPos = _mainCamera.transform.position;
                mouthSource = "Camera";
            }
            else
            {
                Debug.LogWarning("GrabAndEat: No head or camera reference - cannot check distance!");
                return;
            }

            float dist = Vector3.Distance(transform.position, mouthPos);

            _debugLogTimer += Time.deltaTime;
            if (_debugLogTimer >= DEBUG_LOG_INTERVAL)
            {
                _debugLogTimer = 0f;
                Debug.Log("GrabAndEat: dist to mouth = " + dist.ToString("F3")
                    + " | radius = " + mouthRadius
                    + " | source = " + mouthSource);
            }

            if (dist <= mouthRadius)
            {
                Debug.Log("GrabAndEat: Mouth distance reached! Calling SendEatEvent.");
                SendEatEvent();
            }
        }

        private void SendEatEvent()
        {
            _hasBeenEaten = true;

            if (networkEnableObject != null)
            {
                Debug.Log("GrabAndEat: Calling networkEnableObject.SetTargetEnabled(false).");
                networkEnableObject.SetTargetEnabled(false);
            }
            else
            {
                Debug.LogWarning("GrabAndEat: NetworkEnableObject is null! Falling back to local eat.");
                OnNetworkEatReceived(false);
            }
        }

        /// <summary>
        /// Wire this to NetworkEnableObject's _onChange UnityEvent in the Inspector.
        /// Fires on ALL clients when the eat RPC is received.
        /// </summary>
        public void OnNetworkEatReceived(bool isEnabled)
        {
            Debug.Log("GrabAndEat: OnNetworkEatReceived called with isEnabled = " + isEnabled);

            if (isEnabled) return;

            // FIX: guard against multiple clients all scheduling a respawn simultaneously.
            if (_respawnPending)
            {
                Debug.Log("GrabAndEat: Respawn already pending, ignoring duplicate OnNetworkEatReceived.");
                return;
            }
            _respawnPending = true;

            Debug.Log("GrabAndEat: Running eat effects now.");

            if (foodMeshRenderer != null)
            {
                foodMeshRenderer.enabled = false;
                Debug.Log("GrabAndEat: Mesh hidden.");
            }

            if (audioSource != null && eatSFX != null && !_audioPlayed)
            {
                _audioPlayed = true;
                audioSource.PlayOneShot(eatSFX);
            }

            if (eatParticles != null)
            {
                eatParticles.transform.SetParent(null);
                eatParticles.Play();
            }

            // FIX: removed _hasBeenEaten = false here.
            // It now only resets in ShowMesh, after the respawn delay has elapsed.

            Invoke(nameof(ShowMesh), respawnDelay);
        }

        private void ShowMesh()
        {
            _respawnPending = false;

            // FIX: reset eaten state here, not in OnNetworkEatReceived.
            // The object is only ready to be eaten again once it has fully respawned.
            _hasBeenEaten = false;

            _audioPlayed = false;

            if (foodMeshRenderer != null)
            {
                foodMeshRenderer.enabled = true;
                Debug.Log("GrabAndEat: Mesh shown.");
            }

            // FIX: don't teleport if the player is currently holding the object.
            // Teleporting mid-grab causes the stuck/rotating behaviour.
            if (_isGrabbed)
            {
                Debug.Log("GrabAndEat: Skipping teleport - object is currently grabbed.");

                if (networkEnableObject != null)
                    networkEnableObject.SetTargetEnabled(true);

                return;
            }

            if (tableSpawnPoint != null)
            {
                if (_rigidbody != null)
                {
                    _rigidbody.constraints = RigidbodyConstraints.None;
                    _rigidbody.linearVelocity = Vector3.zero;
                    _rigidbody.angularVelocity = Vector3.zero;
                }

                transform.position = tableSpawnPoint.position;
                transform.rotation = tableSpawnPoint.rotation;

                if (_rigidbody != null)
                    _rigidbody.constraints = RigidbodyConstraints.FreezeAll;

                Debug.Log("GrabAndEat: Teleported to table at " + tableSpawnPoint.position);
            }
            else
            {
                Debug.LogWarning("GrabAndEat: tableSpawnPoint not assigned - cannot teleport.");
            }

            if (networkEnableObject != null)
                networkEnableObject.SetTargetEnabled(true);
        }

        private void OnDestroy()
        {
            CancelInvoke(nameof(ShowMesh));

            if (_grabInteractable != null)
            {
                _grabInteractable.selectEntered.RemoveListener(OnGrabbed);
                _grabInteractable.selectExited.RemoveListener(OnReleased);
            }
        }
    }
}