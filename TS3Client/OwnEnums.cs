// TS3Client - A free TeamSpeak3 client implementation
// Copyright (C) 2017  TS3Client contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

namespace TS3Client
{
	using System;

	/*
		* Most important Id datatypes:
		*
		* ClientUid: string
		* ClientDbId: ulong
		* ClientId: ushort
		* ChannelId: ulong
		* ServerGroupId: ulong
		* ChannelGroupId: ulong
		* PermissionIdT: int ???
	*/

	public enum ClientType
	{
		Full = 0,
		Query,
	}

	[Flags]
	public enum ClientListOptions
	{
		// ReSharper disable InconsistentNaming, UnusedMember.Global
		uid = 1 << 0,
		away = 1 << 1,
		voice = 1 << 2,
		times = 1 << 3,
		groups = 1 << 4,
		info = 1 << 5,
		icon = 1 << 6,
		country = 1 << 7,
		// ReSharper restore InconsistentNaming, UnusedMember.Global
	}

	public enum GroupNamingMode
	{
		/// <summary>No group name is displayed.</summary>
		None = 0,
		/// <summary>The group is displayed before the client name.</summary>
		Before,
		/// <summary>The group is displayed after the client name.</summary>
		After
	}

	public enum NotificationType
	{
		Unknown,
		Error,
		// Official notifies, used by client and query
		ChannelCreated,
		ChannelDeleted,
		ChannelChanged,
		ChannelEdited,
		ChannelMoved,
		ChannelPasswordChanged,
		ClientEnterView,
		ClientLeftView,
		ClientMoved,
		ServerEdited,
		TextMessage,
		TokenUsed,

		// Internal notifies, used by client
		InitIvExpand,
		InitServer,
		ChannelList,
		ChannelListFinished,
		ClientNeededPermissions,
		ClientChannelGroupChanged,
		ClientServerGroupAdded,
		ConnectionInfoRequest,
		ConnectionInfo,
		ChannelSubscribed,
		ChannelUnsubscribed,
		ClientChatComposing,
		ServerGroupList,
		ServerGroupsByClientId,
		StartUpload,
		StartDownload,
		FileTransfer,
		FileTransferStatus,
		FileList,
		FileListFinished,
		FileInfo,
		// TODO: notifyclientchatclosed
		// TODO: notifyclientpoke
		// TODO: notifyclientupdated
		// TODO: notifyclientchannelgroupchanged
		// TODO: notifychannelpasswordchanged
		// TODO: notifychanneldescriptionchanged
	}

	// ReSharper disable UnusedMember.Global
	public enum MoveReason
	{
		UserAction = 0,
		UserOrChannelMoved,
		SubscriptionChanged,
		Timeout,
		KickedFromChannel,
		KickedFromServer,
		Banned,
		ServerStopped,
		LeftServer,
		ChannelUpdated,
		ServerOrChannelEdited,
		ServerShutdown,
	}

	public enum GroupWhisperType : byte
	{
		/// <summary>Targets all users in the specified server group.
		/// (Requires servergroup targetId)</summary>
		ServerGroup = 0,
		/// <summary>Targets all users in the specified channel group.
		/// (Requires channelgroup targetId)</summary>
		ChannelGroup,
		/// <summary>Targets all users with channel commander.</summary>
		ChannelCommander,
		/// <summary>Targets all users on the server.</summary>
		AllClients,
	}

	public enum GroupWhisperTarget : byte
	{
		AllChannels = 0,
		CurrentChannel,
		ParentChannel,
		AllParentChannel,
		ChannelFamily,
		CompleteChannelFamily,
		Subchannels,
	}
	// ReSharper enable UnusedMember.Global
}
