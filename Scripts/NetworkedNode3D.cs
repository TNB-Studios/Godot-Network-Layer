using Godot;
using System.Collections.Generic;
using System.Reflection.Metadata.Ecma335;

/// <summary>
/// A Node3D with built-in velocity that automatically updates position each frame.
/// Used for networked objects that need velocity-based movement.
/// </summary>
public partial class NetworkedNode3D : Node3D
{
    public Vector3 Velocity = Vector3.Zero;
    private short _soundToPlay = -1;
    private float timeToResetSound = 0;
    public short SoundToPlay { get => _soundToPlay; }
    public bool CompresedVelocityAndOrientation = false;

    public short currentModelIndex = -1;
    public short currentAnimationIndex = -1;
    public short currentParticleEffectIndex = -1;

    public short attachedToObjectLookupIndex = -1;

    /// <summary>
    /// Sets the model for this networked node by index into the loaded models list.
    /// Removes any existing model children and instantiates the new model.
    /// </summary>
    public void SetModel(short modelIndex, List<PackedScene> loadedModels)
    {
        currentModelIndex = modelIndex;
        if (modelIndex < 0)
        {
            return;
        }
        if (loadedModels.Count < modelIndex || loadedModels[modelIndex] == null)
        {
            GD.Print("Trying to use a not loaded model, index " + modelIndex);
        }

        // Remove any existing model children (nodes with a SceneFilePath)
        foreach (Node child in GetChildren())
        {
            if (!string.IsNullOrEmpty(child.SceneFilePath))
            {
                child.QueueFree();
            }
        }

        // Instantiate and add the new model
        Node3D modelInstance = loadedModels[modelIndex].Instantiate<Node3D>();
        AddChild(modelInstance);
    }

    /// <summary>
    /// Sets the sound to play on this networked node.
    /// On the server side, just records the sound for network transmission.
    /// On the client side, actually plays the sound.
    /// </summary>
    public void SetSound(short soundIndex, List<AudioStream> loadedSounds, float soundRadius = 50.0f, bool soundIs2D = false, bool serverSide = true)
    {
        _soundToPlay = soundIndex;
        SoundRadius = soundRadius;
        SoundIs2D = soundIs2D;

        if (soundIndex != -1)
        {
            if (loadedSounds.Count < soundIndex || loadedSounds[soundIndex] == null)
            {
                GD.Print("Trying to use a not loaded sound, index " + soundIndex);
            }

            AudioStream streamToPlay = loadedSounds[soundIndex];
            timeToResetSound = (Time.GetTicksMsec() / 1000.0f) + (float)streamToPlay.GetLength();

            if (!serverSide)
            {
                // Client side - actually play the sound
                if (soundIs2D)
                {
                    AudioStreamPlayer player = new AudioStreamPlayer();
                    AddChild(player);
                    player.Stream = streamToPlay;
                    player.Finished += () => player.QueueFree();
                    player.Play();
                }
                else
                {
                    AudioStreamPlayer3D player = new AudioStreamPlayer3D();
                    AddChild(player);
                    player.Stream = streamToPlay;
                    player.Finished += () => player.QueueFree();
                    player.MaxDistance = soundRadius;
                    player.UnitSize = soundRadius * 0.15f;
                    player.Play();
                }
            }
        }
        else
        {
            // Stop sound - on client side, remove any audio players
            if (!serverSide)
            {
                foreach (Node child in GetChildren())
                {
                    if (child is AudioStreamPlayer audioPlayer)
                    {
                        audioPlayer.Stop();
                        audioPlayer.QueueFree();
                    }
                    else if (child is AudioStreamPlayer3D audioPlayer3D)
                    {
                        audioPlayer3D.Stop();
                        audioPlayer3D.QueueFree();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Sets the animation for this networked node by index into the loaded animations list.
    /// </summary>
    public void SetAnimation(short animationIndex)
    {
        currentAnimationIndex = animationIndex;
        // TODO: Apply animation when animation system is implemented
    }

    /// <summary>
    /// Sets the particle effect for this networked node by index into the loaded particle effects list.
    /// Removes any existing particle effect children and instantiates the new one.
    /// </summary>
    public void SetParticleEffect(short particleEffectIndex, List<PackedScene> loadedParticleEffects, bool serverSide = true)
    {
        currentParticleEffectIndex = particleEffectIndex;
        if (particleEffectIndex < 0)
        {
            return;
        }
        if (loadedParticleEffects.Count < particleEffectIndex || loadedParticleEffects[particleEffectIndex] == null)
        {
            GD.Print("Trying to use a not loaded particle effect, index " + particleEffectIndex);
        }

        // Remove any existing particle effect children
        foreach (Node child in GetChildren())
        {
            if (child is GpuParticles3D particles3D)
            {
                particles3D.QueueFree();
            }
            else if (child is CpuParticles3D cpuParticles3D)
            {
                cpuParticles3D.QueueFree();
            }
        }

        // Instantiate and add the new particle effect
        Node3D particleInstance = loadedParticleEffects[particleEffectIndex].Instantiate<Node3D>();
        AddChild(particleInstance);
    }

    public float SoundRadius = 50.0f;
    public bool SoundIs2D = false;

    public override void _Process(double delta)
    {
        // If attached to another object, copy its transform instead of using velocity
        if (attachedToObjectLookupIndex != -1)
        {
            ulong parentId = Globals.worldManager_client.networkManager_client.IDToNetworkIDLookup.GetAt(attachedToObjectLookupIndex);
            Node3D parentNode = GodotObject.InstanceFromId(parentId) as Node3D;
            if (parentNode != null)
            {
                GlobalPosition = parentNode.GlobalPosition;
                Rotation = parentNode.Rotation;
                Scale = parentNode.Scale;
            }
        }
        else if (Velocity != Vector3.Zero)
        {
            Position += Velocity * (float)delta;
        }

        // if a sound has finished playing, then reset us back to -1 for the sound being played.
        float now = (Time.GetTicksMsec() / 1000.0f);
        if (timeToResetSound != 0 && timeToResetSound < now)
        {
            timeToResetSound = 0;
            _soundToPlay = -1;
            SoundIs2D = false;
        }
    }
}
