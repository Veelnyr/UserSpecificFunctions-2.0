using System;
using UserSpecificFunctions.Permissions;

namespace UserSpecificFunctions.Database
{
    /// <summary>
    ///     Extends the <see cref="TShockAPI.TSPlayer" /> class.
    /// </summary>
    public sealed class PlayerMetadata
    {
        public const string PlayerInfoKey = "UserSpecificFunctions_PlayerInfo";

        /// <summary>
        ///     Initializes a new instance of the <see cref="PlayerMetadata" /> class with the specified user ID and chat data.
        /// </summary>
        /// <param name="userId">The user ID.</param>
        /// <param name="chatData">The user's chat information, which may not be null.</param>
        public PlayerMetadata(int userId, ChatInformation chatData)
        {
            UserId = userId;
            ChatData = chatData ?? throw new ArgumentNullException(nameof(chatData));
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="PlayerMetadata" /> class with the specified user ID, chat data and
        ///     permissions.
        /// </summary>
        /// <param name="userId">The user ID.</param>
        /// <param name="chatData">The user's chat information, which may not be null.</param>
        /// <param name="permissions">A collection of the user's permissions, which may not be null.</param>
        public PlayerMetadata(int userId, ChatInformation chatData, PermissionCollection permissions) : this(
            userId, chatData)
        {
            Permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        }

        /// <summary>
        ///     Gets or sets the <see cref="ChatData" /> instance associated with this <see cref="PlayerMetadata" /> instance.
        /// </summary>
        public ChatInformation ChatData { get; set; }

        /// <summary>
        ///     Gets or sets the list of custom permissions the user has.
        /// </summary>
        public PermissionCollection Permissions { get; } = new PermissionCollection();

        /// <summary>
        ///     Gets the ID of the user that this instance belongs to.
        /// </summary>
        public int UserId { get; }
    }
}