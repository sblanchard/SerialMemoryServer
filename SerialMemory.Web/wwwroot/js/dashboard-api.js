/**
 * Shared Dashboard API Module
 *
 * Provides consistent API access patterns across all dashboard pages.
 * Uses same-origin paths to go through web admin's /api/* proxy.
 *
 * Usage:
 *   1. Set window.dashboardConfig before loading this script:
 *      window.dashboardConfig = {
 *          apiBaseUrl: '@Model.ApiBaseUrl',      // Public URL for SignalR
 *          serviceApiKey: '@Model.ServiceApiKey', // API key for auth
 *          signalRToken: '@Model.SignalRToken'    // Bearer token
 *      };
 *   2. Use DashboardApi methods:
 *      const data = await DashboardApi.get('/api/memories/recent');
 *      await DashboardApi.post('/api/reasoning/run', { query: '...' });
 */

const DashboardApi = (function() {
    'use strict';

    /**
     * Get configuration from window.dashboardConfig
     */
    function getConfig() {
        return window.dashboardConfig || {};
    }

    /**
     * Build auth headers for API requests
     * @returns {Object} Headers object with Content-Type and auth headers
     */
    function getAuthHeaders() {
        const config = getConfig();
        const headers = {
            'Content-Type': 'application/json'
        };

        if (config.serviceApiKey) {
            headers['X-Api-Key'] = config.serviceApiKey;
        }

        if (config.signalRToken) {
            headers['Authorization'] = `Bearer ${config.signalRToken}`;
        }

        return headers;
    }

    /**
     * Build headers without Content-Type (for GET requests)
     * @returns {Object} Headers object with auth headers only
     */
    function getAuthHeadersNoContent() {
        const headers = getAuthHeaders();
        delete headers['Content-Type'];
        return headers;
    }

    /**
     * Perform a GET request through the proxy
     * @param {string} path - API path (e.g., '/api/memories/recent')
     * @param {Object} options - Additional fetch options
     * @returns {Promise<Response>} Fetch response
     */
    async function get(path, options = {}) {
        const response = await fetch(path, {
            method: 'GET',
            headers: getAuthHeaders(),
            credentials: 'same-origin',
            ...options
        });
        return response;
    }

    /**
     * Perform a GET request and parse JSON response
     * @param {string} path - API path
     * @param {Object} options - Additional fetch options
     * @returns {Promise<any>} Parsed JSON data
     * @throws {Error} If response is not ok
     */
    async function getJson(path, options = {}) {
        const response = await get(path, options);
        if (!response.ok) {
            throw new Error(`API error: ${response.status} ${response.statusText}`);
        }
        return response.json();
    }

    /**
     * Perform a POST request through the proxy
     * @param {string} path - API path
     * @param {Object} body - Request body (will be JSON stringified)
     * @param {Object} options - Additional fetch options
     * @returns {Promise<Response>} Fetch response
     */
    async function post(path, body = null, options = {}) {
        const fetchOptions = {
            method: 'POST',
            headers: getAuthHeaders(),
            credentials: 'same-origin',
            ...options
        };

        if (body !== null) {
            fetchOptions.body = JSON.stringify(body);
        }

        return fetch(path, fetchOptions);
    }

    /**
     * Perform a POST request and parse JSON response
     * @param {string} path - API path
     * @param {Object} body - Request body
     * @param {Object} options - Additional fetch options
     * @returns {Promise<any>} Parsed JSON data
     * @throws {Error} If response is not ok
     */
    async function postJson(path, body = null, options = {}) {
        const response = await post(path, body, options);
        if (!response.ok) {
            const errorText = await response.text();
            throw new Error(`API error: ${response.status} - ${errorText}`);
        }
        return response.json();
    }

    /**
     * Perform a DELETE request through the proxy
     * @param {string} path - API path
     * @param {Object} options - Additional fetch options
     * @returns {Promise<Response>} Fetch response
     */
    async function del(path, options = {}) {
        return fetch(path, {
            method: 'DELETE',
            headers: getAuthHeaders(),
            credentials: 'same-origin',
            ...options
        });
    }

    /**
     * Perform a PUT request through the proxy
     * @param {string} path - API path
     * @param {Object} body - Request body
     * @param {Object} options - Additional fetch options
     * @returns {Promise<Response>} Fetch response
     */
    async function put(path, body = null, options = {}) {
        const fetchOptions = {
            method: 'PUT',
            headers: getAuthHeaders(),
            credentials: 'same-origin',
            ...options
        };

        if (body !== null) {
            fetchOptions.body = JSON.stringify(body);
        }

        return fetch(path, fetchOptions);
    }

    /**
     * Create a SignalR connection to a hub
     * @param {string} hubPath - Hub path (e.g., '/hubs/reasoning')
     * @param {Object} options - SignalR options
     * @returns {HubConnection} SignalR hub connection
     */
    function createSignalRConnection(hubPath, options = {}) {
        const config = getConfig();

        if (typeof signalR === 'undefined') {
            throw new Error('SignalR library not loaded');
        }

        // SignalR needs public URL (WebSocket can't go through HTTP proxy)
        const hubUrl = config.apiBaseUrl + hubPath;

        return new signalR.HubConnectionBuilder()
            .withUrl(hubUrl, {
                accessTokenFactory: () => config.signalRToken,
                ...options
            })
            .withAutomaticReconnect()
            .build();
    }

    /**
     * Get the public API base URL (for SignalR/WebSocket connections)
     * @returns {string} Public API base URL
     */
    function getPublicApiUrl() {
        return getConfig().apiBaseUrl || '';
    }

    /**
     * Check if API key is configured
     * @returns {boolean} True if API key is available
     */
    function hasApiKey() {
        const config = getConfig();
        return !!(config.serviceApiKey && config.serviceApiKey.length > 0);
    }

    // Public API
    return {
        getAuthHeaders,
        getAuthHeadersNoContent,
        get,
        getJson,
        post,
        postJson,
        put,
        del,
        delete: del, // Alias
        createSignalRConnection,
        getPublicApiUrl,
        hasApiKey,
        getConfig
    };
})();

// Export for module systems if available
if (typeof module !== 'undefined' && module.exports) {
    module.exports = DashboardApi;
}
