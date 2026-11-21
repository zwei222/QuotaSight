import { createApp } from '../src/app';
import { fixtureAuthenticator } from './fixture-auth';

export default createApp({ authenticator: fixtureAuthenticator });
