using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#nullable disable
namespace Playnite.Core.Models
{
    /// <summary>
    /// Describes GameSource object.
    /// </summary>
    public class GameSource : DatabaseObject
    {
        /// <summary>
        /// Creates new instance of <see cref="GameSource"/>.
        /// </summary>
        public GameSource(Guid id) : base(id)
        {
        }

        /// <summary>
        /// Creates new instance of <see cref="GameSource"/>.
        /// </summary>
        /// <param name="name"></param>
        public GameSource(Guid id, string name) : base(id)
        {
            Name = name;
        }

        /// <summary>
        /// Gets empty game source.
        /// </summary>
        public static readonly GameSource Empty = new GameSource(Guid.Empty, string.Empty);
    }
}
