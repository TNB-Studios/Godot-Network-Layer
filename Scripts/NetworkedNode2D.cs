using Godot;
using System.Collections.Generic;

/// <summary>
/// A Node2D with built-in velocity that automatically updates position each frame.
/// Used for networked objects that need velocity-based movement.
/// </summary>
public partial class NetworkedNode2D : Node2D, INetworkedNode
{
    public Vector2 Velocity = Vector2.Zero;

    public short SoundToPlay { get; set; } = -1;
    public float SoundRadius { get; set; } = 50.0f;
    public bool SoundIs2D { get; set; } = false;
    public float TimeToResetSound { get; set; } = 0;
    public short currentModelIndex { get; set; } = -1;
    public short currentAnimationIndex { get; set; } = -1;
    public short currentParticleEffectIndex { get; set; } = -1;
    public GpuParticles3D activeParticleEffect { get; set; } = null;
    public short attachedToObjectLookupIndex { get; set; } = -1;
    public byte[] networkedBlob { get; set; } = null;
    public bool CompressedVelocityAndOrientation { get; set; } = false;

    public InterpState PositionInterp { get; } = new InterpState();
    public InterpState OrientationInterp { get; } = new InterpState();
    public InterpState ScaleInterp { get; } = new InterpState();
    public Vector3 GetVelocity3() => new Vector3(Velocity.X, Velocity.Y, 0);

    public bool SetNetworkedBlob(byte[] blob)
    {
        if (blob == null) { networkedBlob = null; return true; }
        if (blob.Length > 255) return false;
        networkedBlob = new byte[blob.Length];
        System.Array.Copy(blob, networkedBlob, blob.Length);
        return true;
    }

    public byte[] GetNetworkedBlob() => networkedBlob;

    public void AttachToObject(Node targetObject)
    {
        if (targetObject == null) { attachedToObjectLookupIndex = -1; return; }
        attachedToObjectLookupIndex = Globals.worldManager_server.networkManager_server.IDToNetworkIDLookup.Find(targetObject.GetInstanceId());
    }

    public void SetModel(short modelIndex, List<PackedScene> loadedModels)
        => NetworkedNodeHelper.SetModel(this, this, modelIndex, loadedModels);

    public void SetSound(short soundIndex, List<AudioStream> loadedSounds, float soundRadius = 50.0f, bool soundIs2D = false, bool serverSide = true)
        => NetworkedNodeHelper.SetSound(this, this, soundIndex, loadedSounds, soundRadius, soundIs2D, serverSide);

    public void SetAnimation(short animationIndex)
        => NetworkedNodeHelper.SetAnimation(this, animationIndex);

    public void SetParticleEffect(short particleEffectIndex, List<PackedScene> loadedParticleEffects, bool serverSide = true)
        => NetworkedNodeHelper.SetParticleEffect(this, this, particleEffectIndex, loadedParticleEffects, serverSide);

    public override void _Process(double delta)
    {
        NetworkedNodeHelper.ProcessAttachedTo(this, this);

        if (attachedToObjectLookupIndex == -1)
        {
            NetworkedNodeHelper.ProcessInterpolation(this, this, (float)delta);
        }

        NetworkedNodeHelper.ProcessSoundReset(this);
        NetworkedNodeHelper.ProcessParticleReset(this);
    }
}
