using System;
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
        [SerializeField] private bool _useGravity = false;
        [SerializeField] private Vector3 _gravityAccel = Vector3.down * 9.8f;
        [SerializeField] private float _gravitySpeedMax = 20f;

        [Header("Ground Detection")]
        [SerializeField] private LayerMask _groundLayerMask = 1 << 0;
        [SerializeField][Min(0f)] private float _groundProbeExtraDistance = 10f;
        [SerializeField][Min(0f)] private float _groundProbeThickness = 0.1f;
        [SerializeField][Min(0f)] private bool _groundProbeFindRealNormal = false;

        [Header("Step")]
        [SerializeField][Min(0f)] private float _stepUpHeight = 0.3f;
        [SerializeField][Min(0f)] private float _stepDownHeight = 0.3f;
        [SerializeField][Min(0f)] private float _stepUpSmooth = 10f;
        [SerializeField][Min(0f)] private float _stepDownSmooth = 10f;
        [SerializeField][Min(0f)] private float _stepSmoothPowerMoving = 2f;

        [Header("Slope")]
        [SerializeField][Range(1f, 90f)] private float _groundAngleLimit = 89f;

        [Header("Debug")]
        [SerializeField] private bool _debugGroundDetection = false;
        [SerializeField] private bool _debugSlopeApproximation = false;

        #endregion

        #region Properties

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

        private float GroundDistanceDesired => _colliderHalfHeight + _stepUpHeight;
        private float GroundDistanceThreshold {
            get {
                float value = GroundDistanceDesired;
                if (IsOnGround) value += _stepDownHeight;
                return value * 1.01f;
            }
        }
        private float GroundProbeDistance => GroundDistanceThreshold + _groundProbeExtraDistance;
        private Vector3 GroundProbeOrigin => _collider.transform.position + new Vector3(0f, GroundDistanceDesired, 0f);

        #endregion

        #region Fields

        #region Cache Fields

        [SerializeField][HideInInspector] private Rigidbody _rigidbody;
        [SerializeField][HideInInspector] private CapsuleCollider _collider;
        private float _colliderHalfHeight;
        // Points with ground normal dot larger than this value is considered as ground.
        private float _minGroundAngleDot;

        private CollisionStore _collisionStore = new CollisionStore();
        private GroundProbeInfo _groundProbeInfo;
        private Vector3 _slopeNormal;
        private Vector3 _velocityGravity;
        private Vector3 _velocityHover;
        private Vector3 _velocityInput;

        private Vector3 _lastNonZeroDirection;
        private float _hoverHeightPatch;

        #endregion

        #region State Fields

        private bool _isOnGround;
        private bool _isTouchingCeiling;
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

        private void OnCollisionStay(Collision collision)
        {
            _collisionStore.Remove(collision);
        }

        private void OnValidate()
        {
            InitComponents();
            InitColliderDimensions();
        }

        private void Awake()
        {
            OnValidate();
            _minGroundAngleDot = Mathf.Cos(_groundAngleLimit * Mathf.Deg2Rad);
            _lastNonZeroDirection = _collider.transform.forward;
        }

        private void FixedUpdate()
        {
            UpdateCollisionCheck();
            UpdateMovement(Time.deltaTime);
            UpdateCleanup();
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
            _colliderHalfHeight = _collider.height / 2f;
        }

        #endregion

        #region Update

        private void UpdateCollisionCheck()
        {
            IsOnGround = GroundSensorUtil.Probe(out _groundProbeInfo,
                GroundProbeOrigin, GroundProbeDistance, _groundProbeThickness, _groundLayerMask, 
                GroundDistanceThreshold, _minGroundAngleDot,
                _groundProbeFindRealNormal,
                _debugGroundDetection);
        }

        private void UpdateMovement(float deltaTime)
        {
            _slopeNormal = Vector3.up;
            if (_velocityInput.magnitude > 0f) _lastNonZeroDirection = _velocityInput.normalized;

            if (IsOnGround)
            {
                // Calculate hover velocity used to maintain step height.
                _velocityHover = CalcHoverVelocity(_groundProbeInfo.Distance, Time.deltaTime);

                // Approximate the slope to move along.
                if(_velocityInput != Vector3.zero)
                {
                    _slopeNormal = GroundSensorUtil.ApproximateSlope(in _groundProbeInfo,
                        GroundProbeOrigin, GroundDistanceThreshold + 1f, _groundLayerMask,
                        _lastNonZeroDirection, 1f, 1, _debugSlopeApproximation);
                }
            }
            else
            {
                _velocityGravity += _gravityAccel * Time.deltaTime;
                _velocityGravity = Vector3.ClampMagnitude(_velocityGravity, _gravitySpeedMax);
            }

            // Assemble the velocity to apply.
            Vector3 velocityMove = AlignVelocityToPlane(_velocityInput, _slopeNormal);
            Vector3 velocityToApply = _velocityGravity + _velocityHover + velocityMove;

            ApplyVelocity(velocityToApply);
        }

        private void UpdateCleanup()
        {
            _collisionStore.Clear();
            _velocityHover = Vector3.zero;
            _velocityInput = Vector3.zero;
        }

        #endregion

        private void ApplyVelocity(Vector3 velocity)
        {
            _rigidbody.linearVelocity = velocity;
        }

        private void OnGroundStateChange(bool newGroundState)
        {
            GroundStateChanged.Invoke(newGroundState);
            _velocityGravity = Vector3.zero;
        }

        #region Helpers

        /// <summary>
        /// Calculate the adjustment floating velocity needed to maintain desired ground distance (step height).
        /// </summary>
        ///   
        private Vector3 CalcHoverVelocity(float groundDistance, float deltaTime,
            float offsetHeight = 0f, bool smoothing = true)
        {
            Vector3 vel = Vector3.zero;
            float hoverHeightPatch = GroundDistanceDesired + offsetHeight - groundDistance;
            if (_isGroundStateChanged || _isTouchingCeiling || !smoothing)
            {
                vel = Vector3.up * (hoverHeightPatch / deltaTime);
            }
            else
            {
                bool shouldGoUp = hoverHeightPatch > 0f;
                float stepSmooth = shouldGoUp ? _stepUpSmooth : _stepDownSmooth;
                if (_velocityInput != Vector3.zero)
                {
                    stepSmooth = Mathf.Pow(stepSmooth, _stepSmoothPowerMoving);
                }
                vel = Vector3.up * (hoverHeightPatch / (deltaTime * stepSmooth));
            }
            _hoverHeightPatch = hoverHeightPatch;
            return vel;
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

    }
}