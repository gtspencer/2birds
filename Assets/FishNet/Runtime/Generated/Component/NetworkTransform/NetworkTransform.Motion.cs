using FishNet.Object;
using FishNet.Transporting;
using GameKit.Dependencies.Utilities;
using UnityEngine;

namespace FishNet.Component.Transforming
{
    public sealed partial class NetworkTransform
    {
        public struct MotionFrame
        {
            public uint Epoch;
            public uint Tick;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Velocity;
            public Vector3 AngularVelocity;
            public sbyte Steering;
            public byte FrontLeft, FrontRight, RearLeft, RearRight;
            public bool Handbrake;
        }

        public bool EpochMotionEnabled { get; private set; }
        public MotionFrame LatestMotion { get; private set; }
        private MotionFrame futureMotion, goalMotion, previousMotion;
        private Vector3 motionGoalStart;
        private Quaternion motionRotationStart;
        private uint motionEpoch;

        public void EnableEpochMotion()
        {
            EpochMotionEnabled = true;
            _componentConfiguration = ComponentConfigurationType.Disabled;
            _clientAuthoritative = true;
            _synchronizePosition = _synchronizeRotation = true;
            _synchronizeParent = _synchronizeScale = false;
            _extrapolation = 0;
            _interpolation = 1;
        }

        public void InstallMotionBaseline(MotionFrame frame)
        {
            if (!EpochMotionEnabled || frame.Epoch < motionEpoch) return;
            motionEpoch = frame.Epoch;
            _lastServerRpcTick = _lastObserversRpcTick = 0;
            _authoritativeClientData.ResetState();
            _toClientChangedWriter?.Clear();
            _lastCalculatedRateData.ResetState();
            _lastSentTransformData?.ResetState();
            _serverChangedSinceReliable = _clientChangedSinceReliable = ChangedDelta.Unset;
            _intervalsRemaining = 0;
            _forceSendTick = FishNet.Managing.Timing.TimeManager.UNSET_TICK;
            _teleport = false;
            _lastReceiveReliable = true;
            while (_goalDataQueue.Count > 0) ResettableObjectCaches<GoalData>.Store(_goalDataQueue.Dequeue());
            ResettableObjectCaches<GoalData>.StoreAndDefault(ref _currentGoalData);
            transform.SetPositionAndRotation(frame.Position, frame.Rotation);
            _lastReceivedClientTransformData?.Update(frame.Tick, frame.Position, frame.Rotation, Vector3.one, frame.Position, null);
            _lastReceivedServerTransformData?.Update(frame.Tick, frame.Position, frame.Rotation, Vector3.one, frame.Position, null);
            LatestMotion = previousMotion = goalMotion = frame;
            motionGoalStart = frame.Position;
            motionRotationStart = frame.Rotation;
            if (futureMotion.Epoch == motionEpoch && futureMotion.Tick > frame.Tick)
                ReceiveMotion(futureMotion, Channel.Unreliable);
            if (futureMotion.Epoch <= motionEpoch) futureMotion = default;
        }

        public void PublishMotion(MotionFrame frame, bool settled)
        {
            if (!EpochMotionEnabled || frame.Epoch != motionEpoch) return;
            LatestMotion = frame;
            if (IsServerInitialized) ObserversMotion(frame, settled ? Channel.Reliable : Channel.Unreliable);
            else if (IsOwner) ServerMotion(frame, settled ? Channel.Reliable : Channel.Unreliable);
        }

        [ServerRpc]
        private void ServerMotion(MotionFrame frame, Channel channel)
        {
            if (!EpochMotionEnabled || frame.Epoch != motionEpoch || frame.Tick <= LatestMotion.Tick) return;
            ReceiveMotion(frame, channel);
            ObserversMotion(frame, channel);
        }

        [ObserversRpc]
        private void ObserversMotion(MotionFrame frame, Channel channel)
        {
            if (!EpochMotionEnabled || IsServerInitialized || IsOwner && frame.Epoch == motionEpoch) return;
            ReceiveMotion(frame, channel);
        }

        private void ReceiveMotion(MotionFrame frame, Channel channel)
        {
            if (frame.Epoch < motionEpoch) return;
            if (frame.Epoch > motionEpoch)
            {
                if (frame.Epoch > futureMotion.Epoch || frame.Epoch == futureMotion.Epoch && frame.Tick > futureMotion.Tick) futureMotion = frame;
                return;
            }
            if (frame.Tick <= LatestMotion.Tick) return;
            var goal = ResettableObjectCaches<GoalData>.Retrieve();
            goal.Motion = frame;
            goal.ReceivedTick = TimeManager.LocalTick;
            goal.Transforms.Update(frame.Tick, frame.Position, frame.Rotation, Vector3.one, frame.Position, null);
            var previous = IsServerInitialized ? _lastReceivedClientTransformData : _lastReceivedServerTransformData;
            SetCalculatedRates(previous, _lastCalculatedRateData, goal, ChangedFull.Position | ChangedFull.Rotation, true, channel);
            previous.Update(goal.Transforms);
            _lastCalculatedRateData.Update(goal.Rates);
            LatestMotion = frame;
            _lastReceiveReliable = channel == Channel.Reliable;
            if (_currentGoalData == null) SetCurrentGoalData(goal);
            else _goalDataQueue.Enqueue(goal);
            while (_goalDataQueue.Count > 4) SetCurrentGoalData(_goalDataQueue.Dequeue());
        }

        private void BeginMotionGoal(MotionFrame frame)
        {
            previousMotion = DisplayMotion;
            motionGoalStart = transform.position;
            motionRotationStart = transform.rotation;
            goalMotion = frame;
        }

        public MotionFrame DisplayMotion
        {
            get
            {
                float distance = Vector3.Distance(motionGoalStart, goalMotion.Position);
                float angle = Quaternion.Angle(motionRotationStart, goalMotion.Rotation);
                float fraction = distance > 0.001f
                    ? 1f - Vector3.Distance(transform.position, goalMotion.Position) / distance
                    : angle > 0.01f ? 1f - Quaternion.Angle(transform.rotation, goalMotion.Rotation) / angle : 1f;
                var result = goalMotion;
                result.Position = transform.position;
                result.Rotation = transform.rotation;
                result.Velocity = Vector3.Lerp(previousMotion.Velocity, goalMotion.Velocity, fraction);
                result.AngularVelocity = Vector3.Lerp(previousMotion.AngularVelocity, goalMotion.AngularVelocity, fraction);
                result.Steering = (sbyte)Mathf.RoundToInt(Mathf.Lerp(previousMotion.Steering, goalMotion.Steering, fraction));
                result.FrontLeft = Blend(previousMotion.FrontLeft, goalMotion.FrontLeft, fraction);
                result.FrontRight = Blend(previousMotion.FrontRight, goalMotion.FrontRight, fraction);
                result.RearLeft = Blend(previousMotion.RearLeft, goalMotion.RearLeft, fraction);
                result.RearRight = Blend(previousMotion.RearRight, goalMotion.RearRight, fraction);
                return result;
            }
        }

        private void ResetEpochMotion()
        {
            motionEpoch = 0;
            LatestMotion = futureMotion = previousMotion = goalMotion = default;
        }

        private static byte Blend(byte a, byte b, float t) => (byte)Mathf.RoundToInt(Mathf.Lerp(a, b, t));
    }
}
