using Godot;
using System;

public unsafe class SharedProperties
{
	public enum SetCompressionOnVectors
	{
		kFull,
		kHalf,
		KCompressed
	}

	public enum ObjectTypeForFrameTransmission
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

	public ObjectTypeForFrameTransmission typeForFrameTransmission = ObjectTypeForFrameTransmission.kSendAll;  // only used on server side. This details if we should bother to send stuff like position per frame if we've already set velocity
	public float ViewRadius { get; set;} = 0; // only used on transmission side.
	public Vector3 Position { get; set; } = Vector3.Zero;
	public Vector3 Orientation { get; set; } = Vector3.Zero;
	public Vector3 Velocity { get; set; } = Vector3.Zero;
	public Vector3 Scale { get; set; } = Vector3.One;

	public short ObjectIndex {get; set; } = 0;

	public short CurrentModel { get; set;} = -1;
	public short CurrentAnimation { get; set; } = -1;

	public short PlayingSound {get; set; } = -1;
	public float SoundRadius {get; set;} = 10; // used server side

	public short ParticleEffect {get; set; } = -1;

	private int currentBufferOffset = 0;
	private byte* currentBuffer = null;

	public enum SharedObjectValueSetMask
	{
		kPosition = 0x01,
		kOrientation = 0x02,
		kVelocity = 0x04,
		kScale = 0x08,
		kSound = 0x10,
		kModel = 0x20,
		kAnimation = 0x40,
		kParticleEffect = 0x80,
		kIs2D = 0x100,          // Stored in top 2 bits of ObjectIndex
		kIs2DInMask = (kIs2D << 6),
		kOnlySendYPos = 0x200,   // Stored in top 2 bits of ObjectIndex
		kOnlySendYposInMask = (kOnlySendYPos << 6)
	}

	// Constants for ObjectIndex bit manipulation
	// Top 2 bits of ObjectIndex are used for kIs2D and kOnlySendYPos flags
	private const short ObjectIndexMask = 0x3FFF;        // 14 bits for actual index (max 16384 objects)

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
			// ObjectIndex already has the kIs2DInMask and kOnlySendYPosInMask flags set in its top bits
			// Just write it directly to the buffer
			*(short*)(currentBuffer) = ObjectIndex;
			currentBufferOffset += 3;
			currentBuffer[2] = 0;   // this is where the mask to show what values are set is.
		}
		currentBuffer[2] |= (byte)newPropertyToAdd;
	}

	unsafe void CopyVectorToBuffer(Vector3 vector, bool is2D = false)
	{
		if (is2D)
		{
			*(Vector2*)(currentBuffer + currentBufferOffset) = new Vector2(vector.X, vector.Y);
			currentBufferOffset += sizeof(float) * 2;
		}
		else
		{
			*(Vector3*)(currentBuffer + currentBufferOffset) = vector;
			currentBufferOffset += sizeof(float) * 3;
		}
	}

	unsafe void CopyFloatToBuffer(float value)
	{
		*(float*)(currentBuffer + currentBufferOffset) = value;
		currentBufferOffset += sizeof(float);
	}

	unsafe void CopyHalfToBuffer(float value)
	{
		*(Half*)(currentBuffer + currentBufferOffset) = (Half)value;
		currentBufferOffset += sizeof(Half);
	}

	unsafe void CopyVectorToBufferAsHalf(Vector3 vector, bool is2D = false)
	{
		*(Half*)(currentBuffer + currentBufferOffset) = (Half)vector.X;
		*(Half*)(currentBuffer + currentBufferOffset + 2) = (Half)vector.Y;
		if (is2D)
		{
			currentBufferOffset += sizeof(Half) * 2;
		}
		else
		{
			*(Half*)(currentBuffer + currentBufferOffset + 4) = (Half)vector.Z;
			currentBufferOffset += sizeof(Half) * 3;
		}
	}

	unsafe void CopyVectorToBufferCompressed(Vector3 vector, bool is2D = false)
	{
		float length = vector.Length();
		*(Half*)(currentBuffer + currentBufferOffset) = (Half)length;
		currentBufferOffset += sizeof(Half);

		if (!is2D)
		{
			Vector3 normalized = (length > 0) ? vector / length : Vector3.Zero;
			currentBuffer[currentBufferOffset] = DirToByte(normalized);
			currentBufferOffset++;
		}
		// For 2D, we only need length since direction is implied by X,Y components
	}

	// Static read functions for use by ReadDataForObject
	static unsafe Vector3 ReadVectorFromBufferStatic(byte* buffer, ref int offset, bool is2D)
	{
		Vector3 result;
		if (is2D)
		{
			result = new Vector3(
				*(float*)(buffer + offset),
				*(float*)(buffer + offset + 4),
				0
			);
			offset += sizeof(float) * 2;
		}
		else
		{
			result = *(Vector3*)(buffer + offset);
			offset += sizeof(float) * 3;
		}
		return result;
	}

	static unsafe Vector3 ReadVectorFromBufferAsHalfStatic(byte* buffer, ref int offset, bool is2D)
	{
		float x = (float)*(Half*)(buffer + offset);
		float y = (float)*(Half*)(buffer + offset + 2);
		float z = 0;
		if (is2D)
		{
			offset += sizeof(Half) * 2;
		}
		else
		{
			z = (float)*(Half*)(buffer + offset + 4);
			offset += sizeof(Half) * 3;
		}
		return new Vector3(x, y, z);
	}

	static unsafe Vector3 ReadVectorFromBufferCompressedStatic(byte* buffer, ref int offset, bool is2D)
	{
		float length = (float)*(Half*)(buffer + offset);
		offset += sizeof(Half);

		if (is2D)
		{
			// For 2D compressed, we don't have a direction byte
			// This mode isn't as useful for 2D, return just the length as X
			return new Vector3(length, 0, 0);
		}
		else
		{
			byte dirByte = buffer[offset];
			offset++;
			Vector3 normalized = ByteToDir(dirByte);
			return normalized * length;
		}
	}

	/// <summary>
	/// Reads object data from buffer and applies to target node.
	/// The mask parameter should include the extended flags (kIs2D, kOnlySendYPos) extracted from ObjectIndex.
	/// </summary>
	public static unsafe int ReadDataForObject(byte* buffer, Node targetNode, short mask)
	{
		int offset = 3; // Skip ObjectIndex (2 bytes) + Mask (1 byte)
		bool is2D = (mask & (short)SharedObjectValueSetMask.kIs2D) != 0;
		bool onlySendYPos = (mask & (short)SharedObjectValueSetMask.kOnlySendYPos) != 0;

		// Get the 3D node for setting properties (or null if 2D)
		Node3D node3D = targetNode as Node3D;
		Node2D node2D = targetNode as Node2D;

        // Velocity
        if ((mask & (short)SharedObjectValueSetMask.kVelocity) != 0)
        {
            Vector3 velocity = ReadVectorByCompression(buffer, ref offset, VelocityCompression, is2D);
            if (targetNode is NetworkedNode3D networkedNode3D)
            {
                networkedNode3D.Velocity = velocity;
            }
            else if (targetNode is NetworkedNode2D networkedNode2D)
            {
                networkedNode2D.Velocity = new Vector2(velocity.X, velocity.Y);
            }
        }

        // Position (never uses compressed format, only Full or Half)
        if ((mask & (short)SharedObjectValueSetMask.kPosition) != 0)
		{
			if (onlySendYPos)
			{
				// Only Y was sent - read it and preserve X/Z
				float y;
				if (PositionCompression == SetCompressionOnVectors.kHalf)
				{
					y = (float)*(Half*)(buffer + offset);
					offset += sizeof(Half);
				}
				else
				{
					y = *(float*)(buffer + offset);
					offset += sizeof(float);
				}

				if (node3D != null)
				{
					Vector3 pos = node3D.GlobalPosition;
					pos.Y = y;
					node3D.GlobalPosition = pos;
				}
				else if (node2D != null)
				{
					Vector2 pos = node2D.GlobalPosition;
					pos.Y = y;
					node2D.GlobalPosition = pos;
				}
			}
			else
			{
				Vector3 position = ReadVectorByCompression(buffer, ref offset, PositionCompression, is2D);
				if (node3D != null)
					node3D.GlobalPosition = position;
				else if (node2D != null)
					node2D.GlobalPosition = new Vector2(position.X, position.Y);
			}
		}

		// Orientation
		if ((mask & (short)SharedObjectValueSetMask.kOrientation) != 0)
		{
			Vector3 orientation = ReadVectorByCompression(buffer, ref offset, OrientationCompression, is2D);
			if (node3D != null)
				node3D.Rotation = orientation;
			else if (node2D != null)
				node2D.Rotation = orientation.Y;  // 2D uses Y as rotation angle
		}

		// Scale
		if ((mask & (short)SharedObjectValueSetMask.kScale) != 0)
		{
			Vector3 scale = ReadVectorByCompression(buffer, ref offset, ScaleCompression, is2D);
			if (node3D != null)
				node3D.Scale = scale;
			else if (node2D != null)
				node2D.Scale = new Vector2(scale.X, scale.Y);
		}



		// Sound
		if ((mask & (short)SharedObjectValueSetMask.kSound) != 0)
		{
			short playingSound;
			if (Globals.worldManager_client.networkManager_client.SoundsUsed.Count > 255)
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
		if ((mask & (short)SharedObjectValueSetMask.kModel) != 0)
		{
			short currentModel;
			if (Globals.worldManager_client.networkManager_client.ModelsUsed.Count > 255)
			{
				currentModel = *(short*)(buffer + offset);
				offset += 2;
			}
			else
			{
				currentModel = buffer[offset];
				offset++;
			}

			// Apply model via callback (handles multi-pass rendering)
			Globals.worldManager_client.networkManager_client.ApplyModelToNode(targetNode, currentModel);
		}

		// Animation
		if ((mask & (short)SharedObjectValueSetMask.kAnimation) != 0)
		{
			short currentAnimation;
			if (Globals.worldManager_client.networkManager_client.AnimationsUsed.Count > 255)
			{
				currentAnimation = *(short*)(buffer + offset);
				offset += 2;
			}
			else
			{
				currentAnimation = buffer[offset];
				offset++;
			}

			// TODO: Apply animation to targetNode (will be synced to depth viewport via SyncNodeProperties)
		}

		// Particle Effect
		if ((mask & (short)SharedObjectValueSetMask.kParticleEffect) != 0)
		{
			short particleEffect;
			if (Globals.worldManager_client.networkManager_client.ParticleEffectsUsed.Count > 255)
			{
				particleEffect = *(short*)(buffer + offset);
				offset += 2;
			}
			else
			{
				particleEffect = buffer[offset];
				offset++;
			}

			// TODO: Apply particle effect to targetNode (will be synced to depth viewport via SyncNodeProperties)
		}

		// Sync all node properties (position, rotation, scale) to other viewports
		Globals.worldManager_client.networkManager_client.SyncNodePropertiesToViewports(targetNode);

		return offset;
	}

	/// <summary>
	/// Helper to read a vector using the specified compression mode.
	/// </summary>
	private static unsafe Vector3 ReadVectorByCompression(byte* buffer, ref int offset, SetCompressionOnVectors compression, bool is2D)
	{
		return compression switch
		{
			SetCompressionOnVectors.kFull => ReadVectorFromBufferStatic(buffer, ref offset, is2D),
			SetCompressionOnVectors.kHalf => ReadVectorFromBufferAsHalfStatic(buffer, ref offset, is2D),
			SetCompressionOnVectors.KCompressed => ReadVectorFromBufferCompressedStatic(buffer, ref offset, is2D),
			_ => ReadVectorFromBufferStatic(buffer, ref offset, is2D)
		};
	}

	/// <summary>
	/// Extracts the full mask (including extended flags) from an encoded object index.
	/// Also returns the actual object index with flags masked off.
	/// </summary>
	public static void DecodeObjectIndexAndMask(short encodedIndex, byte baseMask, out short objectIndex, out short fullMask)
	{
		// Extract the actual object index (lower 14 bits)
		objectIndex = (short)(encodedIndex & ObjectIndexMask);

		// Extract extended flags from top 2 bits (14-15) and shift to mask positions (8-9)
		// kIs2DInObjectIndex (0x4000) -> kIs2D (0x100), kOnlySendYPosInObjectIndex (0x8000) -> kOnlySendYPos (0x200)
		short extendedFlags = (short)((encodedIndex >> 6) & 0x300);

		// Combine base mask with extended flags
		fullMask = (short)((ushort)baseMask | (ushort)extendedFlags);
	}

	public unsafe int ConstructDataForObject(byte* buffer, int spaceLeftInBytes, SharedProperties oldSharedProperties)
	{
		currentBuffer = buffer;
		currentBufferOffset = 0;

		// Extract flags from ObjectIndex (stored in top bits)
		bool is2D = (ObjectIndex & (short)SharedObjectValueSetMask.kIs2DInMask) != 0;
		bool onlySendYPos = (ObjectIndex & unchecked((short)SharedObjectValueSetMask.kOnlySendYposInMask)) != 0;

		// start looking at each value to see what we might put in the buffer for this object.
		// Note: Position and Scale never use compressed format, only Full or Half
		bool sendVelocity = false;
		if (oldSharedProperties == null || CompareVector(Velocity, oldSharedProperties.Velocity))
		{	
			sendVelocity = true;	
			if (oldSharedProperties == null)
			{
				if (Velocity == Vector3.Zero)
				{
					sendVelocity = false;
				}
			}
			if (sendVelocity)
			{
				SetAddedObjectToBuffer(SharedObjectValueSetMask.kVelocity);
				if (VelocityCompression == SetCompressionOnVectors.kFull)
				{
					CopyVectorToBuffer(Velocity, is2D);
				}
				else if (VelocityCompression == SetCompressionOnVectors.kHalf)
				{
					CopyVectorToBufferAsHalf(Velocity, is2D);
				}
				else
				{
					CopyVectorToBufferCompressed(Velocity, is2D);
				}
			}
		}
		if (oldSharedProperties == null || CompareVector(Position, oldSharedProperties.Position))
		{
			bool sendPosition = true;
			if (oldSharedProperties == null)
			{
				if (Position == Vector3.Zero)
				{
					sendPosition = false;
				}
			}

			if (sendPosition)
			{
				// so, if we didn't send a velocity, but there IS a velocity, and it's not the initial set up of the object, don't bother sending a positional update
				// there's no need, because the object is moving anyway. We DO want to send a new position if the velocity has changed, just to be sure the object is in the right place
				if (!sendVelocity && Velocity != Vector3.Zero && oldSharedProperties != null)
				{
					sendPosition = false;
				}
			}
			if (sendPosition)
			{
				SetAddedObjectToBuffer(SharedObjectValueSetMask.kPosition);
				if (onlySendYPos)
				{
					// Only send Y component
					if (PositionCompression == SetCompressionOnVectors.kHalf)
						CopyHalfToBuffer(Position.Y);
					else
						CopyFloatToBuffer(Position.Y);
				}
				else if (PositionCompression == SetCompressionOnVectors.kHalf)
				{
					CopyVectorToBufferAsHalf(Position, is2D);
				}
				else  // kFull (default for position)
				{
					CopyVectorToBuffer(Position, is2D);
				}
			}
		}
		if (oldSharedProperties == null || CompareVector(Orientation, oldSharedProperties.Orientation))
		{
			bool sendOrientation = true;
			if (oldSharedProperties == null)
			{
				if (Orientation == Vector3.Zero)
				{
					sendOrientation = false;
				}
			}
			if (sendOrientation)
			{
				SetAddedObjectToBuffer(SharedObjectValueSetMask.kOrientation);
				if (OrientationCompression == SetCompressionOnVectors.kFull)
				{
					CopyVectorToBuffer(Orientation, is2D);
				}
				else if (OrientationCompression == SetCompressionOnVectors.kHalf)
				{
					CopyVectorToBufferAsHalf(Orientation, is2D);
				}
				else
				{
					CopyVectorToBufferCompressed(Orientation, is2D);
				}
			}
		}
		// Note: Scale never uses compressed format, only Full or Half
		if (oldSharedProperties == null || CompareVector(Scale, oldSharedProperties.Scale))
		{
			bool sendScale = true;
			if (oldSharedProperties == null)
			{
				if (Scale == Vector3.One)
				{
					sendScale = false;
				}
			}
			if (sendScale)
			{
				SetAddedObjectToBuffer(SharedObjectValueSetMask.kScale);
				if (ScaleCompression == SetCompressionOnVectors.kHalf)
				{
					CopyVectorToBufferAsHalf(Scale, is2D);
				}
				else  // kFull (default for scale)
				{
					CopyVectorToBuffer(Scale, is2D);
				}
			}
		}

		if (oldSharedProperties == null || PlayingSound != oldSharedProperties.PlayingSound)
		{
			bool sendSound = true;
			if (oldSharedProperties == null)
			{
				if (PlayingSound == -1)
				{
					sendSound = false;
				}
			}
			if (sendSound)
			{
				SetAddedObjectToBuffer(SharedObjectValueSetMask.kSound);
				if (Globals.worldManager_server.networkManager_server.SoundsUsed.Count > 255)
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
		}
		if (oldSharedProperties == null || CurrentModel != oldSharedProperties.CurrentModel)
		{
			bool sendModel = true;
			if (oldSharedProperties == null)
			{
				if (CurrentModel == -1)
				{
					sendModel = false;
				}
			}
			if (sendModel)
			{
				SetAddedObjectToBuffer(SharedObjectValueSetMask.kModel);
				if (Globals.worldManager_server.networkManager_server.ModelsUsed.Count > 255)
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
		}
		if (oldSharedProperties == null || CurrentAnimation != oldSharedProperties.CurrentAnimation)
		{
			bool sendAnimation = true;
			if (oldSharedProperties == null)
			{
				if (CurrentAnimation == -1)
				{
					sendAnimation = false;
				}
			}
			if (sendAnimation)
			{
				SetAddedObjectToBuffer(SharedObjectValueSetMask.kAnimation);
				if (Globals.worldManager_server.networkManager_server.AnimationsUsed.Count > 255)
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
		}
		if (oldSharedProperties == null || ParticleEffect != oldSharedProperties.ParticleEffect)
		{
			bool sendParticleEffect = true;
			if (oldSharedProperties == null)
			{
				if (ParticleEffect == -1)
				{
					sendParticleEffect = false;
				}
			}
			if (sendParticleEffect)
			{
				SetAddedObjectToBuffer(SharedObjectValueSetMask.kParticleEffect);
				if (Globals.worldManager_server.networkManager_server.ParticleEffectsUsed.Count > 255)
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
	/// Returns the byte size for a vector based on its compression setting and 2D mode.
	/// </summary>
	private static int GetVectorSizeForCompression(SetCompressionOnVectors compression, bool is2D)
	{
		if (is2D)
		{
			return compression switch
			{
				SetCompressionOnVectors.kFull => 8,        // 2 floats (4 bytes each)
				SetCompressionOnVectors.kHalf => 4,        // 2 halfs (2 bytes each)
				SetCompressionOnVectors.KCompressed => 2,  // half magnitude only (no direction for 2D)
				_ => 8
			};
		}
		else
		{
			return compression switch
			{
				SetCompressionOnVectors.kFull => 12,       // 3 floats (4 bytes each)
				SetCompressionOnVectors.kHalf => 6,        // 3 halfs (2 bytes each)
				SetCompressionOnVectors.KCompressed => 3,  // half magnitude (2) + byte direction (1)
				_ => 12
			};
		}
	}

	/*
	/// <summary>
	/// Calculates the total byte size of a SharedProperty entry given its mask.
	/// This works because compression settings are compile-time constants.
	/// The mask should be a short to include the extended flags (kIs2D, kOnlySendYPos).
	/// </summary>
	public static int CalculateSizeFromMask(short mask)
	{
		int size = 3; // ObjectIndex (2 bytes) + Mask (1 byte)
		bool is2D = (mask & (short)SharedObjectValueSetMask.kIs2D) != 0;
		bool onlySendYPos = (mask & (short)SharedObjectValueSetMask.kOnlySendYPos) != 0;

		if ((mask & (short)SharedObjectValueSetMask.kPosition) != 0)
		{
			if (onlySendYPos)
				size += (PositionCompression == SetCompressionOnVectors.kHalf) ? sizeof(Half) : sizeof(float);
			else
				size += GetVectorSizeForCompression(PositionCompression, is2D);
		}

		if ((mask & (short)SharedObjectValueSetMask.kOrientation) != 0)
			size += GetVectorSizeForCompression(OrientationCompression, is2D);

		if ((mask & (short)SharedObjectValueSetMask.kScale) != 0)
			size += GetVectorSizeForCompression(ScaleCompression, is2D);

		if ((mask & (short)SharedObjectValueSetMask.kVelocity) != 0)
			size += GetVectorSizeForCompression(VelocityCompression, is2D);

		if ((mask & (short)SharedObjectValueSetMask.kSound) != 0)
		{
			size += (Globals.networkManager.SoundsUsed.Count > 255) ? 2 : 1;  // Sound index
			size += 2;  // SoundRadius as Half
		}

		if ((mask & (short)SharedObjectValueSetMask.kModel) != 0)
			size += (Globals.networkManager.ModelsUsed.Count > 255) ? 2 : 1;

		if ((mask & (short)SharedObjectValueSetMask.kAnimation) != 0)
			size += (Globals.networkManager.AnimationsUsed.Count > 255) ? 2 : 1;

		if ((mask & (short)SharedObjectValueSetMask.kParticleEffect) != 0)
			size += (Globals.networkManager.ParticleEffectsUsed.Count > 255) ? 2 : 1;

		return size;
	}
	*/
}
