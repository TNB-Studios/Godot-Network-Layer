using Godot;
using System.Collections.Generic;

public static class NetworkedNodeHelper
{
    public static void SetSound(Node ownerNode, INetworkedNode data, short soundIndex, List<AudioStream> loadedSounds,
        float soundRadius = 50.0f, bool soundIs2D = false, bool serverSide = true)
    {
        data.SoundToPlay = soundIndex;
        data.SoundRadius = soundRadius;
        data.SoundIs2D = soundIs2D;

        if (soundIndex != -1)
        {
            if (loadedSounds.Count < soundIndex || loadedSounds[soundIndex] == null)
            {
                GD.Print("Trying to use a not loaded sound, index " + soundIndex);
            }

            AudioStream streamToPlay = loadedSounds[soundIndex];
            data.TimeToResetSound = (Time.GetTicksMsec() / 1000.0f) + (float)streamToPlay.GetLength();

            if (!serverSide)
            {
                if (soundIs2D)
                {
                    AudioStreamPlayer player = new AudioStreamPlayer();
                    ownerNode.AddChild(player);
                    player.Stream = streamToPlay;
                    player.Finished += () => player.QueueFree();
                    player.Play();
                }
                else
                {
                    AudioStreamPlayer3D player = new AudioStreamPlayer3D();
                    ownerNode.AddChild(player);
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
            if (!serverSide)
            {
                foreach (Node child in ownerNode.GetChildren())
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

    public static void SetAnimation(INetworkedNode data, short animationIndex)
    {
        data.currentAnimationIndex = animationIndex;
    }

    public static void SetModel(Node ownerNode, INetworkedNode data, short modelIndex, List<PackedScene> loadedModels)
    {
        data.currentModelIndex = modelIndex;
        if (modelIndex < 0)
        {
            return;
        }
        if (loadedModels.Count < modelIndex || loadedModels[modelIndex] == null)
        {
            GD.Print("Trying to use a not loaded model, index " + modelIndex);
        }

        foreach (Node child in ownerNode.GetChildren())
        {
            if (!string.IsNullOrEmpty(child.SceneFilePath))
            {
                child.QueueFree();
            }
        }

        Node modelInstance = loadedModels[modelIndex].Instantiate<Node>();
        ownerNode.AddChild(modelInstance);
    }

    public static void SetParticleEffect(Node ownerNode, INetworkedNode data, short particleEffectIndex,
        List<PackedScene> loadedParticleEffects, bool serverSide = true)
    {
        data.currentParticleEffectIndex = particleEffectIndex;
        if (particleEffectIndex < 0)
        {
            return;
        }
        if (loadedParticleEffects.Count < particleEffectIndex || loadedParticleEffects[particleEffectIndex] == null)
        {
            GD.Print("Trying to use a not loaded particle effect, index " + particleEffectIndex);
        }

        foreach (Node child in ownerNode.GetChildren())
        {
            if (child is GpuParticles3D || child is CpuParticles3D ||
                child is GpuParticles2D || child is CpuParticles2D)
            {
                child.QueueFree();
            }
        }

        Node particleInstance = loadedParticleEffects[particleEffectIndex].Instantiate<Node>();
        ownerNode.AddChild(particleInstance);
    }

    public static void ProcessSoundReset(INetworkedNode data)
    {
        float now = (Time.GetTicksMsec() / 1000.0f);
        if (data.TimeToResetSound != 0 && data.TimeToResetSound < now)
        {
            data.TimeToResetSound = 0;
            data.SoundToPlay = -1;
            data.SoundIs2D = false;
        }
    }

    public static void ProcessAttachedTo(Node ownerNode, INetworkedNode data)
    {
        if (data.attachedToObjectLookupIndex == -1) return;

        ulong parentId = Globals.worldManager_client.networkManager_client.IDToNetworkIDLookup.GetAt(data.attachedToObjectLookupIndex);
        Node parentNode = GodotObject.InstanceFromId(parentId) as Node;

        if (parentNode is Node3D parent3D && ownerNode is Node3D owner3D)
        {
            owner3D.GlobalPosition = parent3D.GlobalPosition;
            owner3D.Rotation = parent3D.Rotation;
            owner3D.Scale = parent3D.Scale;
        }
        else if (parentNode is Node2D parent2D && ownerNode is Node2D owner2D)
        {
            owner2D.GlobalPosition = parent2D.GlobalPosition;
            owner2D.Rotation = parent2D.Rotation;
            owner2D.Scale = parent2D.Scale;
        }
    }
}
