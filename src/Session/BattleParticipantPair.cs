namespace FreeWorldMultiplayer
{
    // Battle state, entry and checkpoint forwarding all visit exactly two
    // players. Capture both IDs before any send/callback, just as the former
    // new[] { attacker, defender } expression did, but without a heap array.
    internal struct BattleParticipantPair
    {
        private readonly string attacker, defender;

        internal BattleParticipantPair(string attacker, string defender)
        {
            this.attacker = attacker;
            this.defender = defender;
        }

        // A pattern-based value-type enumerator means foreach does not box
        // an IEnumerator or allocate any array on the hot checkpoint path.
        public Enumerator GetEnumerator()
        { return new Enumerator(attacker, defender); }

        internal struct Enumerator
        {
            private readonly string attacker, defender;
            private int index;

            internal Enumerator(string attacker, string defender)
            {
                this.attacker = attacker;
                this.defender = defender;
                index = -1;
            }

            public string Current
            { get { return index == 0 ? attacker : defender; } }

            public bool MoveNext()
            {
                if (index >= 1) return false;
                index++;
                return true;
            }
        }
    }
}
