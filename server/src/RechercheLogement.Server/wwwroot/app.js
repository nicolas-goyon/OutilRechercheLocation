// Fonctions de la page. Blazor les appelle (JS interop), ou les boutons les appellent directement (onclick).
window.rl = {
  /** Copie le texte. Retourne true si la copie a réussi. */
  async copy(text) {
    try {
      // API moderne. Disponible uniquement dans un contexte sécurisé (https ou localhost).
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      // Solution de secours : copie d'un champ texte temporaire.
      const area = document.createElement('textarea');
      area.value = text;
      area.setAttribute('readonly', '');
      Object.assign(area.style, { position: 'fixed', top: '0', left: '0', opacity: '0' });
      document.body.appendChild(area);
      let ok = false;
      try {
        area.select();
        ok = document.execCommand('copy');
      } catch {
        ok = false;
      }
      area.remove();
      return ok;
    }
  },

  /** Copie le token du plugin et affiche le résultat sur le bouton pendant 2 secondes. */
  async copyToken(button) {
    const input = document.getElementById('plugin-token');
    if (!input) return;
    const ok = await rl.copy(input.value);
    if (!ok) {
      // Copie impossible : le token reste sélectionné pour une copie manuelle (Ctrl+C).
      rl.setTokenVisible(true);
      input.select?.();
    }
    rl.flash(button, ok ? 'Copié ✔' : 'Copie impossible : utiliser Ctrl+C');
  },

  /** Affiche ou masque le token du plugin. */
  toggleToken() {
    const input = document.getElementById('plugin-token');
    if (input) rl.setTokenVisible(input.type === 'password');
  },

  /** Affiche (true) ou masque (false) le token du plugin. */
  setTokenVisible(visible) {
    const input = document.getElementById('plugin-token');
    if (!input) return;
    input.type = visible ? 'text' : 'password';
    const toggle = document.querySelector('button[onclick^="rl.toggleToken"]');
    if (toggle) toggle.textContent = visible ? 'Masquer' : 'Afficher';
  },

  /** Remplace le texte d'un bouton pendant 2 secondes. */
  flash(button, text) {
    if (!button) return;
    const label = button.dataset.label ?? button.textContent;
    button.textContent = text;
    clearTimeout(button._rlTimer);
    button._rlTimer = setTimeout(() => {
      button.textContent = label;
    }, 2000);
  },

  /** Ouvre chaque URL dans un nouvel onglet (recherches favorites). Le bloqueur de pop-up peut demander une autorisation. */
  openAll(urls) {
    for (const url of urls) window.open(url, '_blank', 'noopener');
  },

  download(filename, content) {
    const url = URL.createObjectURL(new Blob([content], { type: 'application/json' }));
    const a = Object.assign(document.createElement('a'), { href: url, download: filename });
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 5000);
  },
};
