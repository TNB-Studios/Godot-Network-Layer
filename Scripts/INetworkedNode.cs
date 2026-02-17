using Godot;
using System.Collections.Generic;

public class InterpState
{
    public Vector3 From;
    public Vector3 To;
    public float StartTime;
    public float EndTime; // 0 = not interpolating
}

public interface INetworkedNode
{
    short SoundToPlay { get; set; }
    float SoundRadius { get; set; }
    bool SoundIs2D { get; set; }
    float TimeToResetSound { get; set; }
    short currentModelIndex { get; set; }
    short currentAnimationIndex { get; set; }
    short currentParticleEffectIndex { get; set; }
    short attachedToObjectLookupIndex { get; set; }

    byte[] networkedBlob {get; set;}
    bool CompressedVelocityAndOrientation { get; set; }

    // Interpolation state for smooth network corrections
    InterpState PositionInterp { get; }
    InterpState OrientationInterp { get; }
    InterpState ScaleInterp { get; }
    Vector3 GetVelocity3();

    void SetSound(short soundIndex, List<AudioStream> loadedSounds,
                  float soundRadius = 50.0f, bool soundIs2D = false, bool serverSide = true);
    void SetAnimation(short animationIndex);
    void SetModel(short modelIndex, List<PackedScene> loadedModels);
    void SetParticleEffect(short particleEffectIndex, List<PackedScene> loadedParticleEffects, bool serverSide = true);

    bool SetNetworkedBlob(byte[] blob);
    byte[] GetNetworkedBlob();
    void AttachToObject(Node targetObject);
}
