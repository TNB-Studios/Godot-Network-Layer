using Godot;
using System;
using System.Linq.Expressions;
using System.Numerics;
using System.Runtime.CompilerServices;

public class SharedProperties
{
    enum SetCompressionOnVectors
    {
        kFull,
        kHalf,
        KCompressed
    }

    enum ObjectTypeForFrameTransmission
    {
        kNotMoving,
        kProjectile,
        kSendAll
    }
    
    // these settings determine how the data for specific vectors is stored in the buffer, so it can be modified as needed.
    static readonly SetCompressionOnVectors PositionCompression = SetCompressionOnVectors.kFull;
    static readonly SetCompressionOnVectors OrientationCompression = SetCompressionOnVectors.kFull;
    static readonly SetCompressionOnVectors ScaleCompression = SetCompressionOnVectors.kFull;
    static readonly SetCompressionOnVectors VelocityCompression = SetCompressionOnVectors.kFull;

    public ObjectTypeForFrameTransmission typeForFrameTransmission = kSendAll;  // only used on server side. This details if we should bother to send stuff like position per frame if we've already set velocity
    public float ViewRadius { get; set;} = 0; // only used on transmission side.
    public Vector3 Position { get; set; } = {0,0,0};
    public Vector3 Orientation { get; set; } = {0,0,0};
    public Vector3 Velocity { get; set; } = {0,0,0};
    public Vector3 Scale { get; set; } = {1, 1, 1};

    public short ObjectIndex {get; set; } = 0;

    public short CurrentModel { get; set;} = -1;
    public short CurrentAnimation { get; set; } = -1;

    public short PlayingSound {get; set; } = -1;
    public float SoundRadius {get; set;} = 10; // used server side

    public short ParticleEffect {get; set; } = -1;

    private int currentBufferOffset = 0;
    private byte* currentBuffer = null;
    enum SharedObjectValueSetMask
    {
        kPosition = 0x01,
        kOrientation = 0x2,
        kVelocity = 0x04,
        kScale = 0x08,
        kSound = 0x10,
        kModel = 0x20,
        kAnimation = 0x40,
        kParticleEffect = 0x80
    }

    public SharedProperties()
    {
        Position = Vector3.Zero;
        Orientation = Vector3.Zero;
        Velocity = Vector3.Zero;
        Scale = Vector3.One;
        CurrentAnimation = -1;
        ObjectIndex = 0;
        CurrentModel = -1;
        PlayingSound = -1;
    }

    void SetAddedObjectToBuffer(SharedObjectValueSetMask newPropertyToAdd)
    {
        if (currentBufferOffset == 0)
        {
            *(short*)(currentBuffer) = ObjectIndex;
            currentBufferOffset += 3;
            currentBuffer[2] = 0;   // this is where the mask to show what values are set is.
        }
        currentBuffer[2] |= newPropertyToAdd;
    }

    unsafe void CopyVectorToBuffer(Vector3 vector)
    {
        *(Vector3*)(currentBuffer + currentBufferOffset) = vector;
        currentBufferOffset += sizeof(float) * 3;
    }

    unsafe void CopyVectorToBufferAsHalf(Vector3 vector)
    {
        *(Half*)(currentBuffer + currentBufferOffset) = (Half)vector.X;
        *(Half*)(currentBuffer + currentBufferOffset + 2) = (Half)vector.Y;
        *(Half*)(currentBuffer + currentBufferOffset + 4) = (Half)vector.Z;
        currentBufferOffset += sizeof(Half) * 3;
    }

    unsafe Vector3 ReadVectorFromBuffer()
    {
        Vector3 result = *(Vector3*)(currentBuffer + currentBufferOffset);
        currentBufferOffset += sizeof(float) * 3;
        return result;
    }

    unsafe Vector3 ReadVectorFromBufferAsHalf()
    {
        float x = (float)*(Half*)(currentBuffer + currentBufferOffset);
        float y = (float)*(Half*)(currentBuffer + currentBufferOffset + 2);
        float z = (float)*(Half*)(currentBuffer + currentBufferOffset + 4);
        currentBufferOffset += sizeof(Half) * 3;
        return new Vector3(x, y, z);
    }

    unsafe void CopyVectorToBufferCompressed(Vector3 vector)
    {
        float length = vector.Length();
        *(Half*)(currentBuffer + currentBufferOffset) = (Half)length;
        currentBufferOffset += sizeof(Half);

        Vector3 normalized = (length > 0) ? vector / length : Vector3.Zero;
        currentBuffer[currentBufferOffset] = DirToByte(normalized);
        currentBufferOffset++;
    }

    unsafe Vector3 ReadVectorFromBufferCompressed()
    {
        float length = (float)*(Half*)(currentBuffer + currentBufferOffset);
        currentBufferOffset += sizeof(Half);

        byte dirByte = currentBuffer[currentBufferOffset];
        currentBufferOffset++;

        Vector3 normalized = ByteToDir(dirByte);
        return normalized * length;
    }

    public static unsafe int ReadDataForObject(byte* buffer, Node3D targetNode)
    {
        int offset = 3; // Skip ObjectIndex (2 bytes) + Mask (1 byte)
        byte mask = buffer[2];

        // Position
        if ((mask & (byte)SharedObjectValueSetMask.kPosition) != 0)
        {
            Vector3 position;
            if (PositionCompression == SetCompressionOnVectors.kFull)
            {
                position = *(Vector3*)(buffer + offset);
                offset += sizeof(float) * 3;
            }
            else if (PositionCompression == SetCompressionOnVectors.kHalf)
            {
                position = new Vector3(
                    (float)*(Half*)(buffer + offset),
                    (float)*(Half*)(buffer + offset + 2),
                    (float)*(Half*)(buffer + offset + 4)
                );
                offset += sizeof(Half) * 3;
            }
            else
            {
                float length = (float)*(Half*)(buffer + offset);
                offset += sizeof(Half);
                Vector3 dir = ByteToDir(buffer[offset]);
                offset++;
                position = dir * length;
            }
            targetNode.GlobalPosition = position;
        }

        // Orientation
        if ((mask & (byte)SharedObjectValueSetMask.kOrientation) != 0)
        {
            Vector3 orientation;
            if (OrientationCompression == SetCompressionOnVectors.kFull)
            {
                orientation = *(Vector3*)(buffer + offset);
                offset += sizeof(float) * 3;
            }
            else if (OrientationCompression == SetCompressionOnVectors.kHalf)
            {
                orientation = new Vector3(
                    (float)*(Half*)(buffer + offset),
                    (float)*(Half*)(buffer + offset + 2),
                    (float)*(Half*)(buffer + offset + 4)
                );
                offset += sizeof(Half) * 3;
            }
            else
            {
                float length = (float)*(Half*)(buffer + offset);
                offset += sizeof(Half);
                Vector3 dir = ByteToDir(buffer[offset]);
                offset++;
                orientation = dir * length;
            }
            targetNode.Rotation = orientation;
        }

        // Scale
        if ((mask & (byte)SharedObjectValueSetMask.kScale) != 0)
        {
            Vector3 scale;
            if (ScaleCompression == SetCompressionOnVectors.kFull)
            {
                scale = *(Vector3*)(buffer + offset);
                offset += sizeof(float) * 3;
            }
            else if (ScaleCompression == SetCompressionOnVectors.kHalf)
            {
                scale = new Vector3(
                    (float)*(Half*)(buffer + offset),
                    (float)*(Half*)(buffer + offset + 2),
                    (float)*(Half*)(buffer + offset + 4)
                );
                offset += sizeof(Half) * 3;
            }
            else
            {
                float length = (float)*(Half*)(buffer + offset);
                offset += sizeof(Half);
                Vector3 dir = ByteToDir(buffer[offset]);
                offset++;
                scale = dir * length;
            }
            targetNode.Scale = scale;
        }

        // Velocity
        if ((mask & (byte)SharedObjectValueSetMask.kVelocity) != 0)
        {
            Vector3 velocity;
            if (VelocityCompression == SetCompressionOnVectors.kFull)
            {
                velocity = *(Vector3*)(buffer + offset);
                offset += sizeof(float) * 3;
            }
            else if (VelocityCompression == SetCompressionOnVectors.kHalf)
            {
                velocity = new Vector3(
                    (float)*(Half*)(buffer + offset),
                    (float)*(Half*)(buffer + offset + 2),
                    (float)*(Half*)(buffer + offset + 4)
                );
                offset += sizeof(Half) * 3;
            }
            else
            {
                float length = (float)*(Half*)(buffer + offset);
                offset += sizeof(Half);
                Vector3 dir = ByteToDir(buffer[offset]);
                offset++;
                velocity = dir * length;
            }
            // TODO: Apply velocity to targetNode
        }

        // Sound
        if ((mask & (byte)SharedObjectValueSetMask.kSound) != 0)
        {
            short playingSound;
            if (Globals.networkManager.SoundsUsed.Count > 255)
            {
                playingSound = *(short*)(buffer + offset);
                offset += 2;
            }
            else
            {
                playingSound = buffer[offset];
                offset++;
            }
            float soundRadius = (float)*(Half*)(buffer + offset);
            offset += 2;
            // TODO: Apply sound to targetNode
        }

        // Model
        if ((mask & (byte)SharedObjectValueSetMask.kModel) != 0)
        {
            short currentModel;
            if (Globals.networkManager.ModelsUsed.Count > 255)
            {
                currentModel = *(short*)(buffer + offset);
                offset += 2;
            }
            else
            {
                currentModel = buffer[offset];
                offset++;
            }
            // TODO: Apply model to targetNode
        }

        // Animation
        if ((mask & (byte)SharedObjectValueSetMask.kAnimation) != 0)
        {
            short currentAnimation;
            if (Globals.networkManager.AnimationsUsed.Count > 255)
            {
                currentAnimation = *(short*)(buffer + offset);
                offset += 2;
            }
            else
            {
                currentAnimation = buffer[offset];
                offset++;
            }
            // TODO: Apply animation to targetNode
        }

        // Particle Effect
        if ((mask & (byte)SharedObjectValueSetMask.kParticleEffect) != 0)
        {
            short particleEffect;
            if (Globals.networkManager.ParticleEffectsUsed.Count > 255)
            {
                particleEffect = *(short*)(buffer + offset);
                offset += 2;
            }
            else
            {
                particleEffect = buffer[offset];
                offset++;
            }
            // TODO: Apply particle effect to targetNode
        }

        return offset;
    }

    public unsafe int ConstructDataForObject(byte* buffer, int spaceLeftInBytes, SharedProperties oldSharedProperties)
    {
        currentBuffer = buffer;
        currentBufferOffset = 0;
        // start looking at each value to see what we might put in the buffer for this object.
        if (oldSharedProperties == null || CompareVector(Position, oldSharedProperties.Position))
        {
            SetAddedObjectToBuffer(SharedObjectValueSetMask.kPosition);
            if (PositionCompression == SetCompressionOnVectors.kFull)
            {
                CopyVectorToBuffer(Position);
            }
            else if (PositionCompression == SetCompressionOnVectors.kHalf)
            {
                CopyVectorToBufferAsHalf(Position);
            }
            else
            {
                CopyVectorToBufferCompressed(Position);
            }
        }
        if (oldSharedProperties == null || CompareVector(Orientation, oldSharedProperties.Orientation))
        {
            SetAddedObjectToBuffer(SharedObjectValueSetMask.kOrientation);
            if (OrientationCompression == SetCompressionOnVectors.kFull)
            {
                CopyVectorToBuffer(Orientation);
            }
            else if (OrientationCompression == SetCompressionOnVectors.kHalf)
            {
                CopyVectorToBufferAsHalf(Orientation);
            }
            else
            {
                CopyVectorToBufferCompressed(Orientation);
            }
        }
        if (oldSharedProperties == null || CompareVector(Scale, oldSharedProperties.Scale))
        {
            SetAddedObjectToBuffer(SharedObjectValueSetMask.kScale);
            if (ScaleCompression == SetCompressionOnVectors.kFull)
            {
                CopyVectorToBuffer(Scale);
            }
            else if (ScaleCompression == SetCompressionOnVectors.kHalf)
            {
                CopyVectorToBufferAsHalf(Scale);
            }
            else
            {
                CopyVectorToBufferCompressed(Scale);
            }
        }
        if (oldSharedProperties == null || CompareVector(Velocity, oldSharedProperties.Velocity))
        {
            SetAddedObjectToBuffer(SharedObjectValueSetMask.kVelocity);
            if (VelocityCompression == SetCompressionOnVectors.kFull)
            {
                CopyVectorToBuffer(Velocity);
            }
            else if (VelocityCompression == SetCompressionOnVectors.kHalf)
            {
                CopyVectorToBufferAsHalf(Velocity);
            }
            else
            {
                CopyVectorToBufferCompressed(Velocity);
            }
        }
        if (oldSharedProperties == null || PlayingSound != oldSharedProperties.PlayingSound)
        {
            SetAddedObjectToBuffer(SharedObjectValueSetMask.kSound);
            if (Globals.networkManager.SoundsUsed.Count > 255)
            {
                *(short*)(currentBuffer + currentBufferOffset) = PlayingSound;
                currentBufferOffset += 2;
            }
            else
            {
                currentBuffer[currentBufferOffset] = (byte)PlayingSound;
                currentBufferOffset++;
            }
            *(Half*)(currentBuffer + currentBufferOffset) = (Half)SoundRadius;
            currentBufferOffset += sizeof(Half);
        }
        if (oldSharedProperties == null || CurrentModel != oldSharedProperties.CurrentModel)
        {
            SetAddedObjectToBuffer(SharedObjectValueSetMask.kModel);
            if (Globals.networkManager.ModelsUsed.Count > 255)
            {
                *(short*)(currentBuffer + currentBufferOffset) = CurrentModel;
                currentBufferOffset += 2;
            }
            else
            {
                currentBuffer[currentBufferOffset] = (byte)CurrentModel;
                currentBufferOffset++;
            }
        }
        if (oldSharedProperties == null || CurrentAnimation != oldSharedProperties.CurrentAnimation)
        {
            SetAddedObjectToBuffer(SharedObjectValueSetMask.kAnimation);
            if (Globals.networkManager.AnimationsUsed.Count > 255)
            {
                *(short*)(currentBuffer + currentBufferOffset) = CurrentAnimation;
                currentBufferOffset += 2;
            }
            else
            {
                currentBuffer[currentBufferOffset] = (byte)CurrentAnimation;
                currentBufferOffset++;
            }
        }
        if (oldSharedProperties == null || ParticleEffect != oldSharedProperties.ParticleEffect)
        {
            SetAddedObjectToBuffer(SharedObjectValueSetMask.kParticleEffect);
            if (Globals.networkManager.ParticleEffectsUsed.Count > 255)
            {
                *(short*)(currentBuffer + currentBufferOffset) = ParticleEffect;
                currentBufferOffset += 2;
            }
            else
            {
                currentBuffer[currentBufferOffset] = (byte)ParticleEffect;
                currentBufferOffset++;
            }
        }
        return currentBufferOffset;
    }

    public static bool CompareVector(Vector3 vector1, Vector3 vector2)
    {
        return vector1.X != vector2.X || vector1.Y != vector2.Y || vector1.Z != vector2.Z;
    }

    // Quake III normal vector compression lookup table (162 pre-computed normalized vectors)
    // Source: id-Software/Quake-III-Arena (GPL)
    private static readonly Vector3[] ByteDirs = new Vector3[]
    {
        new Vector3(-0.525731f, 0.000000f, 0.850651f), new Vector3(-0.442863f, 0.238856f, 0.864188f),
        new Vector3(-0.295242f, 0.000000f, 0.955423f), new Vector3(-0.309017f, 0.500000f, 0.809017f),
        new Vector3(-0.162460f, 0.262866f, 0.951056f), new Vector3(0.000000f, 0.000000f, 1.000000f),
        new Vector3(0.000000f, 0.850651f, 0.525731f), new Vector3(-0.147621f, 0.716567f, 0.681718f),
        new Vector3(0.147621f, 0.716567f, 0.681718f), new Vector3(0.000000f, 0.525731f, 0.850651f),
        new Vector3(0.309017f, 0.500000f, 0.809017f), new Vector3(0.525731f, 0.000000f, 0.850651f),
        new Vector3(0.295242f, 0.000000f, 0.955423f), new Vector3(0.442863f, 0.238856f, 0.864188f),
        new Vector3(0.162460f, 0.262866f, 0.951056f), new Vector3(-0.681718f, 0.147621f, 0.716567f),
        new Vector3(-0.809017f, 0.309017f, 0.500000f), new Vector3(-0.587785f, 0.425325f, 0.688191f),
        new Vector3(-0.850651f, 0.525731f, 0.000000f), new Vector3(-0.864188f, 0.442863f, 0.238856f),
        new Vector3(-0.716567f, 0.681718f, 0.147621f), new Vector3(-0.688191f, 0.587785f, 0.425325f),
        new Vector3(-0.500000f, 0.809017f, 0.309017f), new Vector3(-0.238856f, 0.864188f, 0.442863f),
        new Vector3(-0.425325f, 0.688191f, 0.587785f), new Vector3(-0.716567f, 0.681718f, -0.147621f),
        new Vector3(-0.500000f, 0.809017f, -0.309017f), new Vector3(-0.525731f, 0.850651f, 0.000000f),
        new Vector3(0.000000f, 0.850651f, -0.525731f), new Vector3(-0.238856f, 0.864188f, -0.442863f),
        new Vector3(0.000000f, 0.955423f, -0.295242f), new Vector3(-0.262866f, 0.951056f, -0.162460f),
        new Vector3(0.000000f, 1.000000f, 0.000000f), new Vector3(0.000000f, 0.955423f, 0.295242f),
        new Vector3(-0.262866f, 0.951056f, 0.162460f), new Vector3(0.238856f, 0.864188f, 0.442863f),
        new Vector3(0.262866f, 0.951056f, 0.162460f), new Vector3(0.500000f, 0.809017f, 0.309017f),
        new Vector3(0.238856f, 0.864188f, -0.442863f), new Vector3(0.262866f, 0.951056f, -0.162460f),
        new Vector3(0.500000f, 0.809017f, -0.309017f), new Vector3(0.850651f, 0.525731f, 0.000000f),
        new Vector3(0.716567f, 0.681718f, 0.147621f), new Vector3(0.716567f, 0.681718f, -0.147621f),
        new Vector3(0.525731f, 0.850651f, 0.000000f), new Vector3(0.425325f, 0.688191f, 0.587785f),
        new Vector3(0.864188f, 0.442863f, 0.238856f), new Vector3(0.688191f, 0.587785f, 0.425325f),
        new Vector3(0.809017f, 0.309017f, 0.500000f), new Vector3(0.681718f, 0.147621f, 0.716567f),
        new Vector3(0.587785f, 0.425325f, 0.688191f), new Vector3(0.955423f, 0.295242f, 0.000000f),
        new Vector3(1.000000f, 0.000000f, 0.000000f), new Vector3(0.951056f, 0.162460f, 0.262866f),
        new Vector3(0.850651f, -0.525731f, 0.000000f), new Vector3(0.955423f, -0.295242f, 0.000000f),
        new Vector3(0.864188f, -0.442863f, 0.238856f), new Vector3(0.951056f, -0.162460f, 0.262866f),
        new Vector3(0.809017f, -0.309017f, 0.500000f), new Vector3(0.681718f, -0.147621f, 0.716567f),
        new Vector3(0.850651f, 0.000000f, 0.525731f), new Vector3(0.864188f, 0.442863f, -0.238856f),
        new Vector3(0.809017f, 0.309017f, -0.500000f), new Vector3(0.951056f, 0.162460f, -0.262866f),
        new Vector3(0.525731f, 0.000000f, -0.850651f), new Vector3(0.681718f, 0.147621f, -0.716567f),
        new Vector3(0.681718f, -0.147621f, -0.716567f), new Vector3(0.850651f, 0.000000f, -0.525731f),
        new Vector3(0.809017f, -0.309017f, -0.500000f), new Vector3(0.864188f, -0.442863f, -0.238856f),
        new Vector3(0.951056f, -0.162460f, -0.262866f), new Vector3(0.147621f, 0.716567f, -0.681718f),
        new Vector3(0.309017f, 0.500000f, -0.809017f), new Vector3(0.425325f, 0.688191f, -0.587785f),
        new Vector3(0.442863f, 0.238856f, -0.864188f), new Vector3(0.587785f, 0.425325f, -0.688191f),
        new Vector3(0.688191f, 0.587785f, -0.425325f), new Vector3(-0.147621f, 0.716567f, -0.681718f),
        new Vector3(-0.309017f, 0.500000f, -0.809017f), new Vector3(0.000000f, 0.525731f, -0.850651f),
        new Vector3(-0.525731f, 0.000000f, -0.850651f), new Vector3(-0.442863f, 0.238856f, -0.864188f),
        new Vector3(-0.295242f, 0.000000f, -0.955423f), new Vector3(-0.162460f, 0.262866f, -0.951056f),
        new Vector3(0.000000f, 0.000000f, -1.000000f), new Vector3(0.295242f, 0.000000f, -0.955423f),
        new Vector3(0.162460f, 0.262866f, -0.951056f), new Vector3(-0.442863f, -0.238856f, -0.864188f),
        new Vector3(-0.309017f, -0.500000f, -0.809017f), new Vector3(-0.162460f, -0.262866f, -0.951056f),
        new Vector3(0.000000f, -0.850651f, -0.525731f), new Vector3(-0.147621f, -0.716567f, -0.681718f),
        new Vector3(0.147621f, -0.716567f, -0.681718f), new Vector3(0.000000f, -0.525731f, -0.850651f),
        new Vector3(0.309017f, -0.500000f, -0.809017f), new Vector3(0.442863f, -0.238856f, -0.864188f),
        new Vector3(0.162460f, -0.262866f, -0.951056f), new Vector3(0.238856f, -0.864188f, -0.442863f),
        new Vector3(0.500000f, -0.809017f, -0.309017f), new Vector3(0.425325f, -0.688191f, -0.587785f),
        new Vector3(0.716567f, -0.681718f, -0.147621f), new Vector3(0.688191f, -0.587785f, -0.425325f),
        new Vector3(0.587785f, -0.425325f, -0.688191f), new Vector3(0.000000f, -0.955423f, -0.295242f),
        new Vector3(0.000000f, -1.000000f, 0.000000f), new Vector3(0.262866f, -0.951056f, -0.162460f),
        new Vector3(0.000000f, -0.850651f, 0.525731f), new Vector3(0.000000f, -0.955423f, 0.295242f),
        new Vector3(0.238856f, -0.864188f, 0.442863f), new Vector3(0.262866f, -0.951056f, 0.162460f),
        new Vector3(0.500000f, -0.809017f, 0.309017f), new Vector3(0.716567f, -0.681718f, 0.147621f),
        new Vector3(0.525731f, -0.850651f, 0.000000f), new Vector3(-0.238856f, -0.864188f, -0.442863f),
        new Vector3(-0.500000f, -0.809017f, -0.309017f), new Vector3(-0.262866f, -0.951056f, -0.162460f),
        new Vector3(-0.850651f, -0.525731f, 0.000000f), new Vector3(-0.716567f, -0.681718f, -0.147621f),
        new Vector3(-0.716567f, -0.681718f, 0.147621f), new Vector3(-0.525731f, -0.850651f, 0.000000f),
        new Vector3(-0.500000f, -0.809017f, 0.309017f), new Vector3(-0.238856f, -0.864188f, 0.442863f),
        new Vector3(-0.262866f, -0.951056f, 0.162460f), new Vector3(-0.864188f, -0.442863f, 0.238856f),
        new Vector3(-0.809017f, -0.309017f, 0.500000f), new Vector3(-0.688191f, -0.587785f, 0.425325f),
        new Vector3(-0.681718f, -0.147621f, 0.716567f), new Vector3(-0.442863f, -0.238856f, 0.864188f),
        new Vector3(-0.587785f, -0.425325f, 0.688191f), new Vector3(-0.309017f, -0.500000f, 0.809017f),
        new Vector3(-0.147621f, -0.716567f, 0.681718f), new Vector3(-0.425325f, -0.688191f, 0.587785f),
        new Vector3(-0.162460f, -0.262866f, 0.951056f), new Vector3(0.442863f, -0.238856f, 0.864188f),
        new Vector3(0.162460f, -0.262866f, 0.951056f), new Vector3(0.309017f, -0.500000f, 0.809017f),
        new Vector3(0.147621f, -0.716567f, 0.681718f), new Vector3(0.000000f, -0.525731f, 0.850651f),
        new Vector3(0.425325f, -0.688191f, 0.587785f), new Vector3(0.587785f, -0.425325f, 0.688191f),
        new Vector3(0.688191f, -0.587785f, 0.425325f), new Vector3(-0.955423f, 0.295242f, 0.000000f),
        new Vector3(-0.951056f, 0.162460f, 0.262866f), new Vector3(-1.000000f, 0.000000f, 0.000000f),
        new Vector3(-0.850651f, 0.000000f, 0.525731f), new Vector3(-0.955423f, -0.295242f, 0.000000f),
        new Vector3(-0.951056f, -0.162460f, 0.262866f), new Vector3(-0.864188f, 0.442863f, -0.238856f),
        new Vector3(-0.951056f, 0.162460f, -0.262866f), new Vector3(-0.809017f, 0.309017f, -0.500000f),
        new Vector3(-0.864188f, -0.442863f, -0.238856f), new Vector3(-0.951056f, -0.162460f, -0.262866f),
        new Vector3(-0.809017f, -0.309017f, -0.500000f), new Vector3(-0.681718f, 0.147621f, -0.716567f),
        new Vector3(-0.681718f, -0.147621f, -0.716567f), new Vector3(-0.850651f, 0.000000f, -0.525731f),
        new Vector3(-0.688191f, 0.587785f, -0.425325f), new Vector3(-0.587785f, 0.425325f, -0.688191f),
        new Vector3(-0.425325f, 0.688191f, -0.587785f), new Vector3(-0.425325f, -0.688191f, -0.587785f),
        new Vector3(-0.587785f, -0.425325f, -0.688191f), new Vector3(-0.688191f, -0.587785f, -0.425325f)
    };

    /// <summary>
    /// Compresses a normalized direction vector to a single byte index.
    /// Finds the closest matching pre-computed normal from the lookup table.
    /// </summary>
    public static byte DirToByte(Vector3 dir)
    {
        if (dir == Vector3.Zero)
        {
            return 0;
        }

        float bestDot = 0;
        byte best = 0;
        for (int i = 0; i < ByteDirs.Length; i++)
        {
            float dot = dir.X * ByteDirs[i].X + dir.Y * ByteDirs[i].Y + dir.Z * ByteDirs[i].Z;
            if (dot > bestDot)
            {
                bestDot = dot;
                best = (byte)i;
            }
        }
        return best;
    }

    /// <summary>
    /// Decompresses a byte index back to a normalized direction vector.
    /// </summary>
    public static Vector3 ByteToDir(byte b)
    {
        if (b >= ByteDirs.Length)
        {
            return Vector3.Zero;
        }
        return ByteDirs[b];
    }

    /// <summary>
    /// Returns the byte size for a vector based on its compression setting.
    /// </summary>
    private static int GetVectorSizeForCompression(SetCompressionOnVectors compression)
    {
        return compression switch
        {
            SetCompressionOnVectors.kFull => 12,       // 3 floats (4 bytes each)
            SetCompressionOnVectors.kHalf => 6,        // 3 halfs (2 bytes each)
            SetCompressionOnVectors.KCompressed => 3,  // half magnitude (2) + byte direction (1)
            _ => 12
        };
    }

    /// <summary>
    /// Calculates the total byte size of a SharedProperty entry given its mask.
    /// This works because compression settings are compile-time constants.
    /// </summary>
    public static int CalculateSizeFromMask(byte mask)
    {
        int size = 3; // ObjectIndex (2 bytes) + Mask (1 byte)

        if ((mask & (byte)SharedObjectValueSetMask.kPosition) != 0)
            size += GetVectorSizeForCompression(PositionCompression);

        if ((mask & (byte)SharedObjectValueSetMask.kOrientation) != 0)
            size += GetVectorSizeForCompression(OrientationCompression);

        if ((mask & (byte)SharedObjectValueSetMask.kScale) != 0)
            size += GetVectorSizeForCompression(ScaleCompression);

        if ((mask & (byte)SharedObjectValueSetMask.kVelocity) != 0)
            size += GetVectorSizeForCompression(VelocityCompression);

        if ((mask & (byte)SharedObjectValueSetMask.kSound) != 0)
        {
            size += (Globals.networkManager.SoundsUsed.Count > 255) ? 2 : 1;  // Sound index
            size += 2;  // SoundRadius as Half
        }

        if ((mask & (byte)SharedObjectValueSetMask.kModel) != 0)
            size += (Globals.networkManager.ModelsUsed.Count > 255) ? 2 : 1;

        if ((mask & (byte)SharedObjectValueSetMask.kAnimation) != 0)
            size += (Globals.networkManager.AnimationsUsed.Count > 255) ? 2 : 1;

        if ((mask & (byte)SharedObjectValueSetMask.kParticleEffect) != 0)
            size += (Globals.networkManager.ParticleEffectsUsed.Count > 255) ? 2 : 1;

        return size;
    }
}
