using UnityEngine;

namespace TwoBirds
{
    public sealed class AvatarRagdoll : MonoBehaviour
    {
        public Rigidbody Pelvis;
        public Rigidbody[] Bodies;
        public Collider[] Colliders;
        public CharacterJoint[] Joints;
        public Transform[] Bones;
        public Vector3[] RestPositions;
        public Quaternion[] RestRotations;
        private Transform normalParent;
        private Vector3 normalPosition, normalScale;
        private Quaternion normalRotation;
        public bool Active { get; private set; }
        public bool Settled
        {
            get
            {
                foreach (var body in Bodies)
                    if (!body.isKinematic && !body.IsSleeping() &&
                        (body.linearVelocity.sqrMagnitude > 0.0025f || body.angularVelocity.sqrMagnitude > 0.01f)) return false;
                return true;
            }
        }

        internal void Activate(Vector3 root, Vector3 velocity, bool owner)
        {
            if (Active) return;
            Active = true;
            normalParent = transform.parent;
            normalPosition = transform.localPosition;
            normalRotation = transform.localRotation;
            normalScale = transform.localScale;
            transform.SetParent(null, true);
            transform.position += root - Pelvis.position;
            foreach (var joint in Joints)
            {
                joint.enableCollision = false;
                if (joint.connectedBody)
                    Physics.IgnoreCollision(joint.GetComponent<Collider>(), joint.connectedBody.GetComponent<Collider>());
            }
            foreach (var collider in Colliders) collider.enabled = true;
            foreach (var body in Bodies)
            {
                body.detectCollisions = true;
                body.isKinematic = !owner && body == Pelvis;
                if (body.isKinematic) continue;
                body.linearVelocity = velocity;
                body.angularVelocity = Vector3.zero;
            }
        }

        internal void Deactivate()
        {
            if (!Active) return;
            Active = false;
            foreach (var collider in Colliders) collider.enabled = false;
            foreach (var body in Bodies)
            {
                if (!body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
                body.isKinematic = true;
                body.detectCollisions = false;
            }
            transform.SetParent(normalParent, false);
            transform.SetLocalPositionAndRotation(normalPosition, normalRotation);
            transform.localScale = normalScale;
            for (int i = 0; i < Bones.Length; i++) Bones[i].SetLocalPositionAndRotation(RestPositions[i], RestRotations[i]);
        }
    }
}
