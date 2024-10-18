using System;
using UnityEngine;

namespace CC.SRMove
{
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(CapsuleCollider))]
    public partial class AvatarMover : MonoBehaviour
    {
        #region Variables

        #region Inspector Fields

        [Header("Collider")]
        [SerializeField][Min(0f)] private float _height = 2f;
        [SerializeField][Min(0f)] private float _thickness = 1f;
        [SerializeField] private Vector3 _colliderOffset = Vector3.zero;

        [Header("Physics")]
        [SerializeField] private bool _enableGravity = false;
        [SerializeField] private Vector3 _gravityAccel = Vector3.down * 9.8f;
        [SerializeField] private float _gravitySpeedMax = 20f;

        [Header("Ground Detection")]
        [Tooltip("Defines ground layers.")]
        [SerializeField] private LayerMask _groundLayerMask = 1 << 0;
        [SerializeField][Min(0f)] private float _groundProbeExtraDistance = 10f;
        [SerializeField][Min(0f)] private float _groundProbeThickness = 0.1f;
        [Tooltip("If true, while on ground, if ground probe thickness is greater than 0," +
            "fires a secondary raycast towards ground point to obtain actual surface normal.")]
        [SerializeField] private bool _groundProbeFindRealNormal = false;

        [Header("Step")]
        [SerializeField][Min(0f)] private float _stepSmoothDelay = 0.05f;
        [SerializeField][Min(0f)] private float _stepUpHeight = 0.3f;
        [SerializeField][Min(0f)] private float _stepDownHeight = 0.3f;
        [SerializeField][Min(1f)] private float _stepUpSmooth = 3f;
        [SerializeField][Min(1f)] private float _stepDownSmooth = 3f;
        [Tooltip("Step up and step down smoothing multipler while moving.")]
        [SerializeField][Min(0f)] private float _stepSmoothMovingMultipler = 1f;
        [SerializeField] private bool _alignVelocityToSlope = true;
        [Tooltip("Range in front of and behind for ground slope approximation.")]
        [SerializeField][Min(0f)] private float _slopeApproxRange = 1f;
        [SerializeField][Min(0)] private int _slopeApproxIters = 4;

        [Header("Debug")]
        [SerializeField] private bool _debugGroundDetection = false;
        [SerializeField] private bool _debugSlopeApproximation = false;

        #endregion

        #region Properties

        public GroundInfo GroundInfo {
            get => _groundInfo;
            private set {
                _groundInfo = value;
                IsOnGround = value.IsOnGround;
                GroundCollider = value.IsOnGround ? value.Collider : null;
            }
        }
        /// <summary>
        /// Whether the mover is on ground this physics frame.
        /// <para>True if ground probe has detected ground or capsule collider is touching ground.</para>
        /// </summary>
        public bool IsOnGround {
            get => _isOnGround;
            private set {
                if (value != _isOnGround)
                {
                    HandleIsOnGroundChange(value);
                }
                _isOnGround = value;
            }
        }
        public bool IsTouchingCeiling {
            get => _isTouchingCeiling;
            private set {
                if (value == true && value != _isTouchingCeiling)
                {
                    HandleIsTouchingCeilingChange(value);
                }
                _isTouchingCeiling = value;
            }
        }
        public Collider GroundCollider {
            get => _groundCollider;
            private set {
                if (value == null) UnparentFromGround();
                else
                {
                    _groundInfo.Collider.TryGetComponent<Rigidbody>(out _groundRb);
                    ParentToGround(value);
                }
                _groundCollider = value;
            }
        }
        public Vector3 Up {
            get => _up;
            set {
                if (value != _up)
                {
                    HandleUpDirectionChange(value);
                }
                _up = value;
            }
        }
        public bool IsParentedToGround {
            get; private set;
        }
        private ParentableGround GroundParent {
            get => _groundParent;
            set {
                if (value != _groundParent)
                {
                    HandleGroundParentChange(value);
                }
                _groundParent = value;
            }
        }
        private bool IsLeavingGround => _shouldLeaveGround || _velocityLeaveGround != Vector3.zero;
        private float ColliderHalfHeight => _collider.height / 2f;
        /// <summary>
        /// Desired ground distance from the capsule collider center.
        /// </summary>
        private float GroundDistanceDesired => ColliderHalfHeight + _stepUpHeight;
        /// <summary>
        /// Ground probe hits witin this distance from the capsule collider center is considered to be ground.
        /// </summary>
        private float GroundDistanceThreshold {
            get {
                float value = GroundDistanceDesired;
                if (IsOnGround) value += _stepDownHeight;
                return value * 1.01f;
            }
        }
        /// <summary>
        /// Total distance to probe downwards for ground from the capsule collider center.
        /// </summary>
        private float GroundProbeDistance => GroundDistanceThreshold + _groundProbeExtraDistance;
        /// <summary>
        /// Capsule collider center.
        /// </summary>
        private Vector3 GroundProbeOrigin => _collider.transform.position + GroundDistanceDesired * _up;

        #endregion

        #region Fields

        #region Cache Fields

        [SerializeField][HideInInspector] private Rigidbody _rigidbody;
        [SerializeField][HideInInspector] private CapsuleCollider _collider;

        private GroundInfo _collisionGroundInfo = GroundInfo.Empty;
        private GroundInfo _groundInfo = GroundInfo.Empty;
        private bool _isOnGround;
        private bool _isTouchingCeiling = false;
        private bool _collisionIsTouchingWall = false;
        private Collider _groundCollider = null;
        private Rigidbody _groundRb = null;
        private ParentableGround _groundParent = null;
        private Vector3 _slopePoint = Vector3.zero;
        private Vector3 _slopeNormal = Vector3.up;
        private Vector3 _velocityGroundRb = Vector3.zero;
        private Vector3 _velocityGravity = Vector3.zero;
        private Vector3 _velocityHover = Vector3.zero;
        private Vector3 _velocityConstForce = Vector3.zero;
        private Vector3 _velocityLeaveGround = Vector3.zero;
        private Vector3 _velocityInput = Vector3.zero;

        private Vector3 _lastNonZeroDirection = Vector3.forward;
        private float _stepHeightHoverPatch = 0f;
        private float _stepSmoothDelayCounter = 0f;

        #endregion

        #region State Fields

        [SerializeField] private Vector3 _up = Vector3.up;
        private bool _isOnGroundChangedThisFrame;
        private bool _shouldLeaveGround = false;
        private bool _collisionIsTouchingCeiling = false;
        private bool _hasDirectCollision = false;

        #endregion

        #endregion

        #region Events

        public event Action<bool> OnIsOnGroundChanged = delegate { };
        public event Action<bool> OnGroundParentChanged = delegate { };
        public event Action<Vector3> OnUpDirectionChanged = delegate { };
        public event Action<bool> OnIsTouchingCeilingChanged = delegate { };

        #endregion

        #endregion

        #region MonoBehaviour

        private void OnCollisionStay(Collision collision)
        {
            _hasDirectCollision = true;
            CheckDirectCollision(collision, 
                out _collisionGroundInfo,
                out _collisionIsTouchingCeiling,
                out _collisionIsTouchingWall);
        }

        private void OnValidate()
        {
            InitComponents();
            InitColliderDimensions();
        }

        private void Awake()
        {
            OnValidate();
            _lastNonZeroDirection = _collider.transform.forward;
        }

        private void FixedUpdate()
        {
            UpdateCollisionCheck();
            _hasDirectCollision = false;
            UpdateMovement(Time.deltaTime);
            UpdateCleanup();
        }

        private void Update()
        {
            _up = transform.up;
            Debug.DrawLine(GroundProbeOrigin, GroundProbeOrigin + GroundProbeDistance * -_up, Color.green);
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.green;
            Gizmos.DrawSphere(_groundInfo.Point, 0.25f);
        }

        #endregion

        #region Init

        private void InitComponents()
        {
            // Rigidbody.
            TryGetComponent(out _rigidbody);
            _rigidbody.useGravity = false;
            _rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _rigidbody.freezeRotation = true;

            // Mover collider.
            TryGetComponent(out _collider);
        }

        private void InitColliderDimensions()
        {
            ColliderUtil.SetHeight(_collider, _height, _stepUpHeight);
            ColliderUtil.SetThickness(_collider, _thickness);
        }

        #endregion

    }
}