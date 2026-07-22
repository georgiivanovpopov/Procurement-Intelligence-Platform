import React from 'react';
import { createRoot } from 'react-dom/client';
import { createBrowserRouter, RouterProvider } from 'react-router-dom';
import { Landing, LoginPage, ProfilePage, RecordPage, RegisterPage, RouteError, SignalPage } from './screens';
import { CommunityFeedPage, CommunityPostPage, CommunityProfilePage, CommunityRoot, ShareSignalPage } from './community';
import './styles.css';

const router = createBrowserRouter([{
  path: '/',
  element: <CommunityRoot />,
  errorElement: <RouteError />,
  children: [
    { index: true, element: <Landing /> },
    { path: 'register', element: <RegisterPage /> },
    { path: 'login', element: <LoginPage /> },
    { path: 'feed', element: <CommunityFeedPage /> },
    { path: 'users/:username', element: <CommunityProfilePage /> },
    { path: 'posts/:postId', element: <CommunityPostPage /> },
    { path: 'share/:eik/:signalKey', element: <ShareSignalPage /> },
    { path: 'supplier/:eik', element: <ProfilePage /> },
    { path: 'supplier/:eik/signals/:signalKey', element: <SignalPage /> },
    { path: 'supplier/:eik/records/:recordId', element: <RecordPage /> }
  ]
}]);

createRoot(document.getElementById('root')!).render(
  <React.StrictMode><RouterProvider router={router} /></React.StrictMode>
);
