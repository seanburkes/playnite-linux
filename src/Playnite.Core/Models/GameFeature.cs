using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#nullable disable
namespace Playnite.Core.Models
{
    /// <summary>
    /// Describes game feature object.
    /// </summary>
    public class GameFeature : DatabaseObject
    {
        /// <summary>
        /// Creates new instance of <see cref="GameFeature"/>.
        /// </summary>
        public GameFeature(Guid id) : base(id)
        {
        }

        /// <summary>
        /// Creates new instance of <see cref="GameFeature"/>.
        /// </summary>
        /// <param name="name"></param>
        public GameFeature(Guid id, string name) : base(id)
        {
            Name = name;
        }

        /// <summary>
        /// Gets empty tag.
        /// </summary>
        public static readonly GameFeature Empty = new GameFeature(Guid.Empty, string.Empty);
    }
}
