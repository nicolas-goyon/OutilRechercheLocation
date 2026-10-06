import type { InitConfig } from '../src/index';

/** Valeur d'en-tête. `true` -> drapeau seul (`// @noframes`). Tableau -> une ligne par valeur. */
type HeaderValue = string | boolean | string[];

/** Forme de l'export par défaut de chaque userscripts/<id>.mjs. */
export interface UserscriptInstance {
  headers: {
    name: string;
    namespace: string;
    match: string | string[];
    description?: string;
    /** @require supplémentaires, ajoutés après le bundle du plugin. */
    require?: string | string[];
    grant?: string | string[];
    connect?: string | string[];
    noframes?: boolean;
    'run-at'?: 'document-start' | 'document-body' | 'document-end' | 'document-idle' | 'context-menu';
    // Le build génère version, updateURL et downloadURL. Il refuse ces clés ici.
    version?: never;
    updateURL?: never;
    downloadURL?: never;
    [header: string]: HeaderValue | undefined;
  };
  /** Interdit : le token. Le .user.js généré est public. */
  config: Omit<InitConfig, 'apiToken'> & { apiToken?: never };
}
