// <copyright file="GameInputMode.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.RemoteEnums
{
    /// <summary>
    ///     Input and display mode of the game.
    /// </summary>
    public enum GameInputMode
    {
        /// <summary>
        ///     Standard Keyboard and Mouse mode (UiRoot is active).
        /// </summary>
        KeyboardMouse = 0,

        /// <summary>
        ///     Controller mode with a single local player (GamepadUiRoot full-screen container).
        /// </summary>
        ControllerSolo = 1,

        /// <summary>
        ///     Controller mode with 2 local players sharing the same screen (GamepadUiRoot dual containers).
        /// </summary>
        ControllerCoop = 2
    }
}
