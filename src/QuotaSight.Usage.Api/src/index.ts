/**
 * Worker entry point.
 *
 * The authenticator is deliberately the deny-all default: this package ships no
 * deployment credentials, no environment-variable token and no fallback
 * identity. A deployment must compose an explicit Authenticator (real identity
 * verification is still pending design) before any request can succeed.
 */
import { createApp } from './app';
import { UnconfiguredAuthenticator } from './auth';

export default createApp({ authenticator: new UnconfiguredAuthenticator() });
