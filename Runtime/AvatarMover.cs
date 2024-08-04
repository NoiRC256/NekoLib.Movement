using System;
using System.Collections.Generic;
using UnityEngine;

namespace NekoLib.SRMove
{
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(CapsuleCollider))]
    public class AvatarMover : MonoBehaviour
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
        [Tooltip("Range in front of and behind for ground slope approximation.")]
        [SerializeField][Min(0f)] private float _slopeApproxRange = 1f;
        [SerializeField][Min(0)] private int _slopeApproxIters = 4;

        [Header("Debug")]
        [SerializeField] private bool _debugGroundDetection = false;
        [SerializeField] private bool _debugSlopeApproximation = false;

        #endregion

        #region Properties

        /// <summary>
        /// Whether the mover is on ground this physics frame.
        /// <para>True if ground probe has detected ground or capsule collider is touching ground.</para>
        /// </summary>
        public bool IsOnGround {
            get => _isOnGround;
            private set {
                if (value != _isOnGround)
                {
                    OnGroundStateChange(value);
                }
                _isOnGround = value;
            }
        }
        public GroundInfo GroundInfo {
            get => _groundInfo;
            private set {
                _groundInfo = value;
                if (_groundInfo.Collider != null)
                {
                    _groundInfo.Collider.TryGetComponent<Rigidbody>(out _groundRb);
                }
            }
        }
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
        private Vector3 GroundProbeOrigin => _collider.transform.position + new Vector3(0f, GroundDistanceDesired, 0f);

        #endregion

        #region Fields

        #region Cache Fields

        [SerializeField][HideInInspector] private Rigidbody _rigidbody;
        [SerializeField][HideInInspector] private CapsuleCollider _collider;

        private CollisionStore _collisionStore = new CollisionStore();
        private List<Collision> _collisions = new List<Collision>();
        private GroundInfo _groundInfo = GroundInfo.Empty;
        private Rigidbody _groundRb = null;
        private Vector3 _slopePoint = Vector3.zero;
        private Vector3 _slopeNormal = Vector3.up;
        private Vector3 _velocityGroundRb = Vector3.zero;
        private Vector3 _velocityGravity = Vector3.zero;
        private Vector3 _velocityHover = Vector3.zero;
        private Vector3 _velocityInput = Vector3.zero;

        private Vector3 _lastNonZeroDirection = Vector3.forward;
        private float _stepHeightHoverPatch = 0f;
        private float _stepSmoothDelayCounter = 0f;

        #endregion

        #region State Fields

        private bool _isOnGround;
        private bool _isGroundStateChanged;

        #endregion

        #endregion

        #region Events

        public event Action<bool> GroundStateChanged = delegate { };

        #endregion

        #endregion

        #region API

        public void Move(Vector3 velocity)
        {
            _velocityInput = velocity;
        }

        #endregion

        #region MonoBehaviour

        private void OnCollisionEnter(Collision collision)
        {
            _collisions.Add(collision);
        }

        private void OnCollisionExit(Collision collision)
        {
            _collisions.Remove(collision);
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
            UpdateMovement(Time.deltaTime);
            UpdateCleanup();
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.green;
            Gizmos.DrawSphere(_slopePoint, 0.25f);
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

        #region Update

        private void UpdateCollisionCheck()
        {
            GroundInfo groundInfo = GroundInfo.Empty;
            groundInfo = CheckDirectCollisions(out bool isTouchingWall, out bool isTouchingCeiling);
            groundInfo = GroundSensorUtil.Probe(GroundProbeOrigin, GroundProbeDistance, _groundProbeThickness,
                _groundLayerMask, GroundDistanceThreshold,
                _groundProbeFindRealNormal, _debugGroundDetection);

            if (Mathf.Abs(groundInfo.Distance - GroundInfo.Distance) > (0.1f * (_stepUpHeight + _stepDownHeight)))
            {
                _stepSmoothDelayCounter = _stepSmoothDelay;
            }
 
            GroundInfo = groundInfo;
            IsOnGround = groundInfo.IsOnGround;
        }

        private void UpdateMovement(float deltaTime)
        {
            if (_stepSmoothDelayCounter > 0f) _stepSmoothDelayCounter -= deltaTime;
            if (_velocityInput.magnitude > 0f) _lastNonZeroDirection = _velocityInput.normalized;

            if (IsOnGround)
            {
                if (_groundRb != null)
                {
                    _velocityGroundRb = _groundRb.linearVelocity;
                }
                else
                {
                    _velocityGroundRb = Vector3.zero;
                }

                // Approximate the slope to move along.
                if (_velocityInput != Vector3.zero)
                {
                    _slopeNormal = GroundSensorUtil.ApproximateSlope(in _groundInfo, out _slopePoint,
                        GroundProbeOrigin, GroundDistanceThreshold + 1f, _stepUpHeight, _groundLayerMask,
                        _lastNonZeroDirection, _slopeApproxRange, 5, _debugSlopeApproximation);
                }

                // Update hover velocity.
                _velocityHover = UpdateStepHoverVelocity(GroundInfo.Distance, Time.deltaTime);
                _velocityGravity = Vector3.zero;
            }
            else
            {
                _slopeNormal = Vector3.up;
                _velocityGravity += _gravityAccel * Time.deltaTime;
                _velocityGravity = Vector3.ClampMagnitude(_velocityGravity, _gravitySpeedMax);
            }

            // Assemble the velocity to apply.
            Vector3 velocityMove = AlignVelocityToPlane(_velocityInput, _slopeNormal);
            Vector3 velocityToApply = _velocityGroundRb + _velocityGravity + _velocityHover + velocityMove;

            ApplyVelocity(velocityToApply);
        }

        private void UpdateCleanup()
        {
            _collisionStore.Clear();
            _velocityHover = Vector3.zero;
            _velocityInput = Vector3.zero;
            _isGroundStateChanged = false;
        }

        #endregion

        private void ApplyVelocity(Vector3 velocity)
        {
            _rigidbody.linearVelocity = velocity;
        }

        private void OnGroundStateChange(bool newGroundState)
        {
            _isGroundStateChanged = true;
            GroundStateChanged.Invoke(newGroundState);
            _velocityGravity = Vector3.zero;
        }

        #region Helpers

        #region Velocity Calculation Helpers

        /// <summary>
        /// Calculate the adjustment hover velocity required to maintain desired ground distance (step height).
        /// </summary>
        ///   
        private Vector3 UpdateStepHoverVelocity(float groundDistance, float deltaTime,
            float groundDistanceOffset = 0f, bool smoothing = true)
        {
            Vector3 vel = Vector3.zero;
            float hoverHeightPatch = GroundDistanceDesired + groundDistanceOffset - groundDistance;
            _stepHeightHoverPatch = hoverHeightPatch;
            if (_isGroundStateChanged || !smoothing)
            {
                vel = Vector3.up * (hoverHeightPatch / deltaTime);
            }
            else
            {
                if (_stepSmoothDelayCounter >= 0f)
                {
                    return Vector3.zero;
                }
                float stepSmooth = hoverHeightPatch > 0f ? _stepUpSmooth : _stepDownSmooth;
                if (_velocityInput != Vector3.zero)
                {
                    stepSmooth = stepSmooth * _stepSmoothMovingMultipler;
                }
                float directionSign = Mathf.Sign(hoverHeightPatch);
                float hoverHeightDelta = Sigmoid(Mathf.Abs(hoverHeightPatch)) / (stepSmooth * deltaTime);
                vel = Vector3.up * directionSign * hoverHeightDelta;
            }
            return vel;
        }

        public static float Sigmoid(float value)
        {
            return value;
        }

        /// <summary>
        /// Align velocity to a plane defined by the specified plane normal.
        /// </summary>
        /// <param name="velocity"></param>
        /// <param name="normal"></param>
        /// <returns></returns>
        private Vector3 AlignVelocityToPlane(Vector3 velocity, Vector3 normal)
        {
            float speed = velocity.magnitude;
            Vector3 alignedDirection = Quaternion.FromToRotation(Vector3.up, normal) * (velocity / speed);
            return speed * alignedDirection.normalized;
        }

        #endregion

        #region Direct Collision Check Helpers

        private GroundInfo CheckDirectCollisions(out bool isTouchingWall, out bool isTouchingCeiling)
        {
            GroundInfo groundInfo = GroundInfo.Empty;
            isTouchingWall = false;
            isTouchingCeiling = false;
            Vector3 accGroundNormal = Vector3.up;
            int groundCollisionCount = 0;
            for (int i = 0; i < _collisions.Count; i++)
            {
                Collision collision = _collisions[i];
                if (!LayerMaskContains(_groundLayerMask, collision.collider.gameObject.layer))
                {
                    break;
                }
                if (collision.contactCount == 0) continue;
                ContactPoint contact = collision.GetContact(0);
                if (contact.normal.y > 0.0001f)
                {
                    groundInfo.IsOnGround = true;
                    groundCollisionCount++;
                }
                else if (contact.normal.y < 0.0001f)
                {
                    isTouchingCeiling = true;
                }
                else
                {
                    isTouchingWall = true;
                }
            }
            if (groundCollisionCount > 0)
            {
                groundInfo.Normal = accGroundNormal / groundCollisionCount;
            }
            return groundInfo;
        }
        #endregion

        private bool LayerMaskContains(LayerMask layerMask, int layer)
        {
            return ((layerMask & (1 << layer)) != 0);
        }

        #endregion

    }
}