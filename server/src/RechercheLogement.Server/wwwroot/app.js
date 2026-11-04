// Fonctions de la page. Blazor les appelle (JS interop), ou les boutons les appellent directement (onclick).
window.rl = {
  /** Préférences de ce navigateur. Le stockage peut être indisponible : la page fonctionne alors sans. */
  prefs: {
    get(key) {
      try {
        return localStorage.getItem(key);
      } catch {
        return null;
      }
    },
    set(key, value) {
      try {
        if (value === null || value === undefined) localStorage.removeItem(key);
        else localStorage.setItem(key, value);
      } catch {
        // Préférence non mémorisée.
      }
    },
  },

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

  /**
   * Carte de la zone de recherche (composant ZoneMap). Leaflet est chargé depuis unpkg (App.razor),
   * le fond de carte vient d'OpenStreetMap. Un clic ou le déplacement du marqueur renvoie le point à Blazor.
   */
  zoneMap: {
    maps: new Map(),

    init(el, dotnet) {
      if (!window.L || !el) return false;
      const map = L.map(el, { scrollWheelZoom: true }).setView([46.6, 2.4], 5);
      L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">contributeurs OpenStreetMap</a>',
      }).addTo(map);
      const layer = L.featureGroup().addTo(map);
      const zones = L.featureGroup().addTo(map);
      const state = { map, layer, zones, zoneLayers: new Map(), dotnet, fitKey: null, generation: 0, unavailable: '[]' };
      map.on('click', (e) => dotnet.invokeMethodAsync('Point', e.latlng.lat, e.latlng.lng));
      rl.zoneMap.maps.set(el, state);
      // Le bloc peut encore changer de taille juste après l'affichage.
      setTimeout(() => map.invalidateSize(), 50);
      return true;
    },

    update(el, json) {
      const st = rl.zoneMap.maps.get(el);
      if (!st) return;
      const s = JSON.parse(json);
      const color = getComputedStyle(document.documentElement).getPropertyValue('--accent').trim() || '#2563eb';
      st.layer.clearLayers();
      for (const ring of s.rings ?? []) {
        L.polygon(ring, { color, weight: 2, fillOpacity: 0.12 }).addTo(st.layer);
      }
      if (s.center) {
        if (s.radiusM) L.circle(s.center, { radius: s.radiusM, color, weight: 2, fillOpacity: 0.1 }).addTo(st.layer);
        const marker = L.marker(s.center, { draggable: true, title: 'Point de départ (glisser pour le déplacer)' }).addTo(st.layer);
        marker.on('dragend', () => {
          const p = marker.getLatLng();
          st.dotnet.invokeMethodAsync('Point', p.lat, p.lng);
        });
      }
      for (const m of s.markers ?? []) {
        L.circleMarker([m.lat, m.lon], { radius: 7, color: '#16a34a', weight: 2, fillOpacity: 0.6 })
          .bindTooltip(m.label)
          .addTo(st.layer);
      }
      const fit = s.fitKey !== st.fitKey;
      st.fitKey = s.fitKey;
      rl.zoneMap.syncZones(st, s.zones ?? [], color).then((current) => {
        if (current && fit) rl.zoneMap.fit(st);
      });
    },

    /** Recadre la carte sur tout ce qui est affiché. */
    fit(st) {
      const b = st.layer.getBounds();
      if (st.zones.getLayers().length > 0) b.extend(st.zones.getBounds());
      if (!b.isValid()) return;
      if (b.getNorthEast().equals(b.getSouthWest())) st.map.setView(b.getCenter(), 12);
      else st.map.fitBounds(b, { padding: [24, 24], maxZoom: 13 });
    },

    /**
     * Affiche le contour de chaque lieu. Les contours sont gardés entre deux mises à jour. Contour indisponible :
     * un point au centre du lieu, s'il est connu, et le nom du lieu est signalé à Blazor.
     * Retourne false si une mise à jour plus récente a pris le relais.
     */
    async syncZones(st, zones, color) {
      const generation = ++st.generation;
      const wanted = new Set(zones.map((z) => z.key));
      for (const [key, layer] of st.zoneLayers) {
        if (wanted.has(key)) continue;
        st.zones.removeLayer(layer);
        st.zoneLayers.delete(key);
      }
      const missing = [];
      await Promise.all(zones.map(async (z) => {
        if (st.zoneLayers.has(z.key)) return;
        let layer = null;
        try {
          const res = await fetch(z.url);
          if (!res.ok) throw new Error(String(res.status));
          layer = L.geoJSON(await res.json(), { style: { color, weight: 2, fillOpacity: 0.14 } }).bindTooltip(z.label, { sticky: true });
        } catch {
          missing.push(z.label);
          if (z.center) layer = L.circleMarker(z.center, { radius: 7, color: '#16a34a', weight: 2, fillOpacity: 0.6 }).bindTooltip(z.label);
        }
        if (layer && generation === st.generation) {
          st.zoneLayers.set(z.key, layer);
          st.zones.addLayer(layer);
        }
      }));
      if (generation !== st.generation) return false;
      const unavailable = JSON.stringify(missing.sort());
      if (unavailable !== st.unavailable) {
        st.unavailable = unavailable;
        st.dotnet.invokeMethodAsync('ZonesUnavailable', missing);
      }
      return true;
    },

    dispose(el) {
      const st = rl.zoneMap.maps.get(el);
      if (!st) return;
      st.map.remove();
      rl.zoneMap.maps.delete(el);
    },
  },
};
