using System.Collections.Generic;
using FishNet.Serializing;
using UnityEngine;

namespace TwoBirds
{
    public struct CartContactState
    {
        public int Cart;
        public uint Generation;
        public Vector3 Normal;
        public bool Launched;
    }

    public struct CartContactSet
    {
        public const int Capacity = 8;
        public byte Count;
        private CartContactState a, b, c, d, e, f, g, h;
        public CartContactState this[int index]
        {
            get => index switch { 0 => a, 1 => b, 2 => c, 3 => d, 4 => e, 5 => f, 6 => g, _ => h };
            set
            {
                switch (index)
                {
                    case 0: a = value; break;
                    case 1: b = value; break;
                    case 2: c = value; break;
                    case 3: d = value; break;
                    case 4: e = value; break;
                    case 5: f = value; break;
                    case 6: g = value; break;
                    default: h = value; break;
                }
            }
        }
    }

    public static class CartContactSerializers
    {
        public static void WriteCartContactSet(this Writer writer, CartContactSet value)
        {
            writer.WriteUInt8Unpacked(value.Count);
            for (int i = 0; i < value.Count; i++) writer.Write(value[i]);
        }

        public static CartContactSet ReadCartContactSet(this Reader reader)
        {
            CartContactSet value = new() { Count = reader.ReadUInt8Unpacked() };
            for (int i = 0; i < value.Count; i++) value[i] = reader.Read<CartContactState>();
            return value;
        }
    }

    public sealed partial class PlayerMotor
    {
        private const float CartSeparation = 0.03f;
        private CartContactSet cartContacts;
        private readonly Collider[] nearbyCarts = new Collider[32];
        private readonly HashSet<int> checkedCarts = new();
        private readonly float[] upwardTargets = new float[CartContactSet.Capacity];
        private Vector3 preContactVelocity, preContactPosition;
        private float preContactYaw;
        private float pendingCartLift, cartRecovery;
        private int pendingCartSource = -1;
        private uint pendingCartGeneration, contactRestoreTick;
        private bool cartTookOff, restoreCartContacts, restoringCartHistory;
        private bool pendingCartDrop;
        private uint pendingCartDropRevision;
        private int cartMask, exitGraceCart = -1;

        private bool ContactPhysicsActive => !Suspended && capsule.enabled && !Body.isKinematic &&
            !(PredictionManager.IsReconciling && rejectReplay) && NetworkObject.RigidbodyPauser?.Paused != true;

        private void ClearCartContacts()
        {
            cartContacts = default;
            pendingCartLift = cartRecovery = 0f;
            pendingCartSource = -1;
            pendingCartGeneration = 0;
            cartTookOff = restoringCartHistory = false;
            pendingCartDrop = false;
            exitGraceCart = -1;
            restoreCartContacts = true;
        }

        internal void SuppressExitLaunch(GolfCartNetwork cart)
        {
            exitGraceCart = cart ? cart.ObjectId : -1;
            restoreCartContacts = true;
        }

        internal void CartGenerationChanged(GolfCartNetwork cart)
        {
            if (pendingCartSource == cart.ObjectId && pendingCartLift > 0f)
                pendingCartLift = cartRecovery = 0f;
            for (int i = 0; i < cartContacts.Count; i++)
            {
                var entry = cartContacts[i];
                if (entry.Cart != cart.ObjectId) continue;
                entry.Generation = cart.ContactGeneration;
                entry.Launched = true;
                cartContacts[i] = entry;
            }
            if (!Suspended && TouchesCart(cart, out var normal)) AddCartContact(cart, normal, true);
            restoreCartContacts = true;
        }

        internal void ForgetCartContact(int id)
        {
            if (pendingCartSource == id && pendingCartLift > 0f) pendingCartLift = cartRecovery = 0f;
            for (int i = cartContacts.Count - 1; i >= 0; i--)
                if (cartContacts[i].Cart == id) RemoveCartContact(i);
            if (exitGraceCart == id) exitGraceCart = -1;
        }

        private int AddCartContact(GolfCartNetwork cart, Vector3 normal, bool baseline)
        {
            int index = 0;
            while (index < cartContacts.Count && cartContacts[index].Cart < cart.ObjectId) index++;
            if (index < cartContacts.Count && cartContacts[index].Cart == cart.ObjectId)
            {
                var entry = cartContacts[index];
                entry.Launched |= baseline || entry.Generation != cart.ContactGeneration;
                entry.Generation = cart.ContactGeneration;
                entry.Normal = normal;
                cartContacts[index] = entry;
                return index;
            }
            if (cartContacts.Count == CartContactSet.Capacity) return -1;
            for (int i = cartContacts.Count; i > index; i--)
            {
                cartContacts[i] = cartContacts[i - 1];
                upwardTargets[i] = upwardTargets[i - 1];
            }
            cartContacts.Count++;
            cartContacts[index] = new CartContactState
            {
                Cart = cart.ObjectId, Generation = cart.ContactGeneration, Normal = normal,
                Launched = baseline || exitGraceCart == cart.ObjectId
            };
            upwardTargets[index] = 0f;
            return index;
        }

        private void RemoveCartContact(int index)
        {
            for (int i = index + 1; i < cartContacts.Count; i++)
            {
                cartContacts[i - 1] = cartContacts[i];
                upwardTargets[i - 1] = upwardTargets[i];
            }
            cartContacts.Count--;
        }

        private void RestoreNearbyCartContacts()
        {
            checkedCarts.Clear();
            RefreshCartSeparation(true);
            CapsuleEnds(out var bottom, out var top);
            int count = Physics.OverlapCapsuleNonAlloc(bottom, top, capsule.radius + CartSeparation,
                nearbyCarts, cartMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var body = nearbyCarts[i].attachedRigidbody;
                if (body && GolfCartNetwork.Bodies.TryGetValue(body, out var cart) && checkedCarts.Add(cart.ObjectId) && cart.SimulatesPhysics &&
                    TouchesCart(cart, out var normal)) AddCartContact(cart, normal, restoringCartHistory && cart.BaselineTick >= contactRestoreTick);
            }
            restoreCartContacts = false;
            if (!PredictionManager.IsReconciling) restoringCartHistory = false;
        }

        private void CapsuleEnds(out Vector3 bottom, out Vector3 top)
        {
            Vector3 center = Body.position + Body.rotation * capsule.center;
            float half = Mathf.Max(0f, capsule.height * 0.5f - capsule.radius);
            bottom = center - Vector3.up * half;
            top = center + Vector3.up * half;
        }

        private bool TouchesCart(GolfCartNetwork cart, out Vector3 normal)
        {
            normal = default;
            if (!cart.SimulatesPhysics) return false;
            CapsuleEnds(out var bottom, out var top);
            float deepest = -1f;
            foreach (var box in cart.Controller.Solids)
            {
                if (!box.enabled) continue;
                if (Physics.ComputePenetration(capsule, Body.position, Body.rotation, box,
                    box.transform.position, box.transform.rotation, out var direction, out float distance))
                {
                    if (distance > deepest) { normal = direction; deepest = distance; }
                    continue;
                }
                Vector3 center = (bottom + top) * 0.5f;
                Vector3 point = box.ClosestPoint(center);
                point.y = Mathf.Clamp(point.y, bottom.y, top.y);
                Vector3 axis = new(center.x, point.y, center.z);
                Vector3 gap = axis - box.ClosestPoint(axis);
                if (deepest < 0f && Physics.ComputePenetration(capsule, Body.position - gap.normalized * CartSeparation,
                    Body.rotation, box, box.transform.position, box.transform.rotation, out direction, out _))
                { normal = direction; deepest = 0f; }
            }
            return deepest >= 0f;
        }

        private void RefreshCartSeparation(bool restoring = false)
        {
            bool exitChecked = false;
            for (int i = cartContacts.Count - 1; i >= 0; i--)
            {
                var entry = cartContacts[i];
                if (!GolfCartNetwork.Carts.TryGetValue(entry.Cart, out var cart)) { RemoveCartContact(i); continue; }
                // Paused replay partners provide no evidence of separation.
                if (!cart.SimulatesPhysics) continue;
                if (restoring) checkedCarts.Add(entry.Cart);
                if (entry.Cart == exitGraceCart) exitChecked = true;
                if (!TouchesCart(cart, out var normal))
                {
                    if (entry.Cart == exitGraceCart) exitGraceCart = -1;
                    RemoveCartContact(i);
                    continue;
                }
                entry.Launched |= entry.Generation != cart.ContactGeneration ||
                    restoring && restoringCartHistory && cart.BaselineTick >= contactRestoreTick;
                entry.Generation = cart.ContactGeneration;
                entry.Normal = normal;
                cartContacts[i] = entry;
            }
            if (!exitChecked && exitGraceCart >= 0 && (!GolfCartNetwork.Carts.TryGetValue(exitGraceCart, out var exited) ||
                exited.SimulatesPhysics && !TouchesCart(exited, out _))) exitGraceCart = -1;
        }

        private Vector3 LimitCartEffort(Vector3 change, Vector3 intent, float delta)
        {
            for (int i = 0; i < cartContacts.Count; i++)
            {
                var entry = cartContacts[i];
                if (!GolfCartNetwork.Carts.TryGetValue(entry.Cart, out var cart) || !cart.SimulatesPhysics) continue;
                if (Mathf.Abs(entry.Normal.y) > 0.7f) continue;
                Vector3 normal = Vector3.ProjectOnPlane(entry.Normal, Vector3.up).normalized;
                float into = Vector3.Dot(change, normal);
                float limit = Vector3.Dot(intent, normal) < -0.001f ? settings.CartPushAcceleration * delta : 0f;
                if (into < -limit) change += normal * (-limit - into);
            }
            return change;
        }

        private void BeforeContactPhysics(float delta)
        {
            if (!ContactPhysicsActive) return;
            preContactVelocity = Body.linearVelocity;
            preContactPosition = Body.position;
            preContactYaw = Body.rotation.eulerAngles.y;
            System.Array.Clear(upwardTargets, 0, upwardTargets.Length);
        }

        private void OnCollisionEnter(Collision collision) => GatherCartContact(collision);
        private void OnCollisionStay(Collision collision) => GatherCartContact(collision);

        private void GatherCartContact(Collision collision)
        {
            if (!ContactPhysicsActive || !collision.rigidbody ||
                !GolfCartNetwork.Bodies.TryGetValue(collision.rigidbody, out var cart) || !cart.SimulatesPhysics) return;
            for (int i = 0; i < collision.contactCount; i++)
            {
                var point = collision.GetContact(i);
                if (point.thisCollider != capsule) continue;
                Vector3 normal = point.normal;
                int index = AddCartContact(cart, normal, false);
                if (index < 0 || cartContacts[index].Launched || Mathf.Abs(normal.y) > 0.7f) continue;
                normal = Vector3.ProjectOnPlane(normal, Vector3.up).normalized;
                Vector3 velocity = cart.Controller.PreContactVelocity(point.point);
                float approach = Mathf.Max(0f, Vector3.Dot(velocity, normal));
                float relative = Mathf.Max(0f, Vector3.Dot(velocity - preContactVelocity, normal));
                float severity = Mathf.Min(approach, relative);
                var tuning = cart.Controller.Settings;
                float blend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(tuning.LaunchSpeed, 2f * tuning.LaunchSpeed, severity));
                upwardTargets[index] = Mathf.Max(upwardTargets[index], tuning.LaunchLift * severity * blend);
            }
        }

        private void AfterContactPhysics(float delta)
        {
            if (!ContactPhysicsActive) return;
            float target = 0f;
            int source = -1;
            uint generation = 0;
            for (int i = 0; i < cartContacts.Count; i++)
            {
                if (upwardTargets[i] <= 0f) continue;
                var entry = cartContacts[i];
                entry.Launched = true;
                cartContacts[i] = entry;
                if (upwardTargets[i] > target)
                {
                    target = upwardTargets[i];
                    source = entry.Cart;
                    generation = entry.Generation;
                }
            }
            if (target > 0f)
            {
                if (carry && carry.IsCarrying && (IsOwner || IsServerInitialized))
                {
                    if (PredictionManager.IsReconciling)
                    {
                        pendingCartDrop = true;
                        pendingCartDropRevision = ControlRevision;
                    }
                    else carry.ImpactDrop(preContactPosition, preContactYaw);
                }
                pendingCartLift = Mathf.Max(0f, target - Body.linearVelocity.y);
                pendingCartSource = source;
                pendingCartGeneration = generation;
                cartRecovery = Mathf.Max(cartRecovery, Mathf.Clamp(2f * target / Physics.gravity.magnitude, 0.35f, 1.2f));
                cartTookOff = false;
            }
            RefreshCartSeparation();
        }

        private void StepCartRecovery(bool grounded, float delta)
        {
            if (cartRecovery <= 0f) return;
            cartRecovery = Mathf.Max(0f, cartRecovery - delta);
            if (!grounded) cartTookOff = true;
            else if (cartTookOff) cartRecovery = 0f;
        }
    }
}
