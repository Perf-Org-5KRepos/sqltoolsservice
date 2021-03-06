//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

namespace Microsoft.SqlTools.DataProtocol.Contracts.Connection
{
    /// <summary>
    /// Parameters for the List Databases Request.
    /// </summary>
    public class ListDatabasesParams
    {
        /// <summary>
        /// URI of the owner of the connection requesting the list of databases.
        /// </summary>
        public string OwnerUri { get; set; }
    }
}
