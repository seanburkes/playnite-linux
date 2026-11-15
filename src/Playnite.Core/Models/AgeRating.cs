using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#nullable disable
namespace Playnite.Core.Models
{
    /// <summary>
    /// Describes age rating object.
    /// </summary>
    public class AgeRating : DatabaseObject
    {
        /// <summary>
        /// Creates new instance of <see cref="AgeRating"/>.
        /// </summary>
        public AgeRating(Guid id) : base(id)
        {
        }

        /// <summary>
        /// Creates new instance of <see cref="AgeRating"/>.
        /// </summary>
        /// <param name="name">Rating name.</param>
        public AgeRating(Guid id, string name) : base(id)
        {
            Name = name;
        }

        /// <summary>
        /// Gets empty age rating.
        /// </summary>
        public static readonly AgeRating Empty = new AgeRating(Guid.Empty, string.Empty);
    }
}
