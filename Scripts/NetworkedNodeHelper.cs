using Godot;
using System.Collections.Generic;

public static class NetworkedNodeHelper
{
    private const float InterpThreshold = 0.01f;
    private const float InterpDuration = 0.1f; // 100ms

    private static float AngleDistance(float a, float b)
    {
        float diff = b - a;
        // Wrap to [-PI, PI]
        diff = ((diff + Mathf.Pi) % (Mathf.Pi * 2)) - Mathf.Pi;
        if (diff < -Mathf.Pi) diff += Mathf.Pi * 2;
        return Mathf.Abs(diff);
    }

    private static float MaxAngleDistance(Vector3 a, Vector3 b)
    {
        return Mathf.Max(AngleDistance(a.X, b.X), Mathf.Max(AngleDistance(a.Y, b.Y), AngleDistance(a.Z, b.Z)));
    }

    private static Vector3 LerpAngles(Vector3 from, Vector3 to, float t)
    {
        return new Vector3(
            Mathf.LerpAngle(from.X, to.X, t),
            Mathf.LerpAngle(from.Y, to.Y, t),
            Mathf.LerpAngle(from.Z, to.Z, t)
        );
    }
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

    // --- Interpolation: called from ReadDataForObject when server values arrive ---

    public static void ApplyNetworkPosition(Node ownerNode, INetworkedNode data, Vector3 serverPosition)
    {
        Vector3 currentPos;
        if (ownerNode is Node3D n3d) currentPos = n3d.GlobalPosition;
        else if (ownerNode is Node2D n2d) currentPos = new Vector3(n2d.GlobalPosition.X, n2d.GlobalPosition.Y, 0);
        else return;

        if ((serverPosition - currentPos).Length() > InterpThreshold)
        {
            float now = Time.GetTicksMsec() / 1000.0f;
            data.PositionInterp.From = currentPos;
            data.PositionInterp.To = serverPosition;
            data.PositionInterp.StartTime = now;
            data.PositionInterp.EndTime = now + InterpDuration;
        }
        else
        {
            if (ownerNode is Node3D node3D) node3D.GlobalPosition = serverPosition;
            else if (ownerNode is Node2D node2D) node2D.GlobalPosition = new Vector2(serverPosition.X, serverPosition.Y);
            data.PositionInterp.EndTime = 0;
        }
    }

    public static void ApplyNetworkOrientation(Node ownerNode, INetworkedNode data, Vector3 serverOrientation)
    {
        Vector3 currentOri;
        if (ownerNode is Node3D n3d) currentOri = n3d.Rotation;
        else if (ownerNode is Node2D n2d) currentOri = new Vector3(0, n2d.Rotation, 0);
        else return;

        if (MaxAngleDistance(serverOrientation, currentOri) > InterpThreshold)
        {
            float now = Time.GetTicksMsec() / 1000.0f;
            data.OrientationInterp.From = currentOri;
            data.OrientationInterp.To = serverOrientation;
            data.OrientationInterp.StartTime = now;
            data.OrientationInterp.EndTime = now + InterpDuration;
        }
        else
        {
            if (ownerNode is Node3D node3D) node3D.Rotation = serverOrientation;
            else if (ownerNode is Node2D node2D) node2D.Rotation = serverOrientation.Y;
            data.OrientationInterp.EndTime = 0;
        }
    }

    public static void ApplyNetworkScale(Node ownerNode, INetworkedNode data, Vector3 serverScale)
    {
        Vector3 currentScale;
        if (ownerNode is Node3D n3d) currentScale = n3d.Scale;
        else if (ownerNode is Node2D n2d) currentScale = new Vector3(n2d.Scale.X, n2d.Scale.Y, 1);
        else return;

        if ((serverScale - currentScale).Length() > InterpThreshold)
        {
            float now = Time.GetTicksMsec() / 1000.0f;
            data.ScaleInterp.From = currentScale;
            data.ScaleInterp.To = serverScale;
            data.ScaleInterp.StartTime = now;
            data.ScaleInterp.EndTime = now + InterpDuration;
        }
        else
        {
            if (ownerNode is Node3D node3D) node3D.Scale = serverScale;
            else if (ownerNode is Node2D node2D) node2D.Scale = new Vector2(serverScale.X, serverScale.Y);
            data.ScaleInterp.EndTime = 0;
        }
    }

    // --- Interpolation: called each frame from _Process ---

    public static void ProcessInterpolation(Node ownerNode, INetworkedNode data, float frameDelta)
    {
        float now = Time.GetTicksMsec() / 1000.0f;
        Vector3 velocityDelta = data.GetVelocity3() * frameDelta;

        // Position interpolation
        if (data.PositionInterp.EndTime > 0)
        {
            // Both endpoints advance with velocity
            data.PositionInterp.From += velocityDelta;
            data.PositionInterp.To += velocityDelta;

            if (now >= data.PositionInterp.EndTime)
            {
                // Done — snap to target
                if (ownerNode is Node3D n3d) n3d.GlobalPosition = data.PositionInterp.To;
                else if (ownerNode is Node2D n2d) n2d.GlobalPosition = new Vector2(data.PositionInterp.To.X, data.PositionInterp.To.Y);
                data.PositionInterp.EndTime = 0;
            }
            else
            {
                float t = (now - data.PositionInterp.StartTime) / (data.PositionInterp.EndTime - data.PositionInterp.StartTime);
                Vector3 lerped = data.PositionInterp.From.Lerp(data.PositionInterp.To, t);
                if (ownerNode is Node3D n3d) n3d.GlobalPosition = lerped;
                else if (ownerNode is Node2D n2d) n2d.GlobalPosition = new Vector2(lerped.X, lerped.Y);
            }
        }
        else
        {
            // Not interpolating — normal velocity movement
            if (velocityDelta != Vector3.Zero)
            {
                if (ownerNode is Node3D n3d) n3d.Position += velocityDelta;
                else if (ownerNode is Node2D n2d) n2d.Position += new Vector2(velocityDelta.X, velocityDelta.Y);
            }
        }

        // Orientation interpolation (no velocity, angle-aware lerp)
        if (data.OrientationInterp.EndTime > 0)
        {
            if (now >= data.OrientationInterp.EndTime)
            {
                if (ownerNode is Node3D n3d) n3d.Rotation = data.OrientationInterp.To;
                else if (ownerNode is Node2D n2d) n2d.Rotation = data.OrientationInterp.To.Y;
                data.OrientationInterp.EndTime = 0;
            }
            else
            {
                float t = (now - data.OrientationInterp.StartTime) / (data.OrientationInterp.EndTime - data.OrientationInterp.StartTime);
                Vector3 lerped = LerpAngles(data.OrientationInterp.From, data.OrientationInterp.To, t);
                if (ownerNode is Node3D n3d) n3d.Rotation = lerped;
                else if (ownerNode is Node2D n2d) n2d.Rotation = lerped.Y;
            }
        }

        // Scale interpolation (no velocity)
        if (data.ScaleInterp.EndTime > 0)
        {
            if (now >= data.ScaleInterp.EndTime)
            {
                if (ownerNode is Node3D n3d) n3d.Scale = data.ScaleInterp.To;
                else if (ownerNode is Node2D n2d) n2d.Scale = new Vector2(data.ScaleInterp.To.X, data.ScaleInterp.To.Y);
                data.ScaleInterp.EndTime = 0;
            }
            else
            {
                float t = (now - data.ScaleInterp.StartTime) / (data.ScaleInterp.EndTime - data.ScaleInterp.StartTime);
                Vector3 lerped = data.ScaleInterp.From.Lerp(data.ScaleInterp.To, t);
                if (ownerNode is Node3D n3d) n3d.Scale = lerped;
                else if (ownerNode is Node2D n2d) n2d.Scale = new Vector2(lerped.X, lerped.Y);
            }
        }
    }

    public static void ProcessAttachedTo(Node ownerNode, INetworkedNode data)
    {
        if (data.attachedToObjectLookupIndex == -1) return;

        ulong parentId = Globals.worldManager_client.networkManager_client.IDToNetworkIDLookup.GetAt(data.attachedToObjectLookupIndex);
        Node parentNode = GodotObject.InstanceFromId(parentId) as Node;

        // If we're already a child of the attached object (default reparent behavior),
        // we get transform for free from the scene tree — no need to copy.
        if (parentNode == null || ownerNode.GetParent() == parentNode) return;

        // Cross-viewport or non-reparented case: manually copy transform from parent
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
