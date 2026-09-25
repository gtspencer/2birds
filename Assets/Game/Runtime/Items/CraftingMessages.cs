using System.Collections.Generic;
using FishNet.Broadcast;
using UnityEngine;

namespace TwoBirds
{
    public enum CauldronPhase : byte { Empty, Occupied, Brewing, Rising, Ready, Failed, Disposing }
    public struct IngredientRecord
    {
        public byte Definition;
        public int Player;
        public uint WorldId, Operation, StartTick;
        public Vector3 Position;
        public Quaternion Rotation;
    }
    public struct CauldronRecord
    {
        public int Id;
        public uint Revision, StartTick, Deadline, Output;
        public byte Result;
        public CauldronPhase Phase;
        public List<IngredientRecord> Ingredients;
    }
    public struct PotionActivation
    {
        public uint Id, StartTick, Expiry, SourceItem, Operation, CartLifetime;
        public int Player, Cart;
        public byte Definition;
        public Vector3 Position;
    }
    public struct PotionDose
    {
        public int Player;
        public uint Lifetime, Reset, Revision, Effect, StartTick, Expiry, OwnerTick, SimulationTick;
        public byte Definition;
    }
    public struct CraftingTransition : IBroadcast
    {
        public uint Epoch;
        public bool Snapshot, HasCauldron, HasActivation, Blast, HasSplat;
        public List<ItemRecord> Items;
        public CauldronRecord Cauldron;
        public PotionActivation Activation;
        public SplatEvent Splat;
    }
    public struct PotionDoseMessage : IBroadcast { public uint Epoch; public PotionDose Dose; }
    public struct ItemContactResult : IBroadcast
    {
        public uint Epoch, Item, Operation;
        public int Releaser;
        public bool HasSplat, SplatAccepted;
    }
    public struct ItemContact : IBroadcast
    {
        public uint Epoch, Item, Revision, Operation, CartLifetime;
        public int Releaser, Cauldron, Cart;
        public bool Impact, HasSplat;
        public SplatTarget Target;
        public Vector3 SplatPoint, SplatVelocity;
        public Quaternion SplatRotation;
        public Vector3 Position;
    }
}
