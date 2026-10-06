import type { InitConfig } from '../src/index';

/** Valeur d'en-tête : `true` -> drapeau seul (`// @noframes`), tableau -> une ligne par valeur. */
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
    // version / updateURL / downloadURL sont générés — le build les refuse.
    version?: never;
    updateURL?: never;
    downloadURL?: never;
    [header: string]: HeaderValue | undefined;
  };
  /** Le token n'a rien à faire ici : le .user.js généré est publié. */
  config: Omit<InitConfig, 'apiToken'> & { apiToken?: never };
}
